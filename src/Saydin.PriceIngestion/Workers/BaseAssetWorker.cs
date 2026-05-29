using System.Text.Json;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Tüm asset worker'larının ortak backfill + zamanlama mantığı.
/// Her worker BackfillStartDate, ChunkDays ve DailyRunUtcTime'ı override eder.
/// appsettings.json → IngestionWorkers:{WorkerConfigKey}:DailyRunUtcHour/Minute ile saatler override edilebilir.
///
/// Her veri çekme operasyonu `ingestion_jobs` tablosuna start/finish kaydı yazar
/// (CLAUDE.md "ingestion_jobs tablosuna başarı ve hata durumları yazılır" zorunluluğu).
/// </summary>
public abstract class BaseAssetWorker(
    IExternalPriceAdapter adapter,
    IPriceIngestionRepository repository,
    IIngestionJobRepository jobs,
    IConfiguration configuration,
    ILogger logger)
{
    protected abstract DateOnly BackfillStartDate { get; }
    protected abstract int ChunkDays { get; }

    /// <summary>Config section adı: "Tcmb", "CoinGecko", "OpenExchangeRates", "TwelveData"</summary>
    protected abstract string WorkerConfigKey { get; }

    /// <summary>
    /// Varsayılan günlük çalışma saati. appsettings ile override edilebilir.
    /// </summary>
    protected abstract TimeOnly DefaultDailyRunUtcTime { get; }

    private TimeOnly DailyRunUtcTime
    {
        get
        {
            var section = configuration.GetSection($"IngestionWorkers:{WorkerConfigKey}");
            var hour    = section.GetValue<int?>("DailyRunUtcHour");
            var minute  = section.GetValue<int?>("DailyRunUtcMinute");
            return (hour.HasValue || minute.HasValue)
                ? new TimeOnly(hour ?? DefaultDailyRunUtcTime.Hour, minute ?? DefaultDailyRunUtcTime.Minute)
                : DefaultDailyRunUtcTime;
        }
    }

    /// <summary>
    /// Chunk'lar arası bekleme süresi. Rate-limit'i olan API'ler override eder.
    /// </summary>
    protected virtual TimeSpan ChunkDelay => TimeSpan.Zero;

    /// <summary>
    /// "Today" anlamı kaynağa göre değişebilir (review F1.1-10).
    /// TCMB / TwelveData / OpenExchangeRates: UTC bugün — son yayın gün-içi gelir.
    /// CoinGecko: UTC kapanışı 00:00 UTC iken; 02:00 UTC çekimde "yesterday"
    /// son kapanmış kripto gününü verir. Adapter-specific worker bu metodu override eder.
    /// </summary>
    protected virtual DateOnly TargetDate(DateTime utcNow) =>
        DateOnly.FromDateTime(utcNow.Date);

    /// <summary>
    /// F2.4-9 ([G-D-04]): Varsayılan olarak <c>false</c> — backfill yalnız "latestDate
    /// sonrasını" kovalar (orijinal davranış). Override edilirse <see cref="BackfillAsync"/>
    /// `[BackfillStartDate, yesterday]` aralığındaki tüm boşlukları DB'de var olmayan
    /// tarih kümesinden hedefler. Hafta sonu/tatil "boş" olduğu kaynaklar (TCMB, OXR)
    /// bunu açmamalı — gereksiz API çağrısı yaratır. Crypto (CoinGecko, 7/24 piyasa)
    /// için uygundur.
    /// </summary>
    protected virtual bool EnableGapAwareBackfill => false;

    public async Task RunAsync(CancellationToken ct)
    {
        await BackfillAsync(ct);

        // F1.1-11 / P1R-005: Backfill bittiğinde TargetDate'in verisi henüz yoksa
        // derhal çek. Önceki kod yalnızca `IsScheduledTimePassedToday()` kontrolü
        // yapıyordu; uzun bir backfill gece yarısını aşarsa (örn. 23:00→04:00 ertesi
        // gün) scheduled time hâlâ bugün gelmemiş gibi görünür ve günlük veri 24 saate
        // kadar eksik kalırdı. Persisted-state tabanlı kontrol (latestStored < target)
        // saat-bağımsız ve idempotent.
        if (!ct.IsCancellationRequested && await IsImmediateFetchNeededAsync(ct))
        {
            try
            {
                await FetchTodayAsync(ct);
            }
            // PR #11 follow-up: yalnızca shutdown token tetiklenmişse worker'ı durdur.
            // Non-shutdown OperationCanceledException (örn. internal HttpClient/Polly
            // timeout) burada yutulursa worker kalıcı olarak durur ve ertesi günler
            // hiç veri akmaz; transient cancel sebeplerini orchestrator'a sızdır.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // Codacy follow-up: ilk fetch için de aynı exponential backoff
                // mantığı; transient olmayan bug/config error rethrow edilir.
                if (!await TryRecoverWithBackoffAsync(ex, ct))
                    return;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun();
            logger.LogInformation("{Source} sonraki çekim: {NextRun:HH:mm} UTC ({Delay:hh\\:mm} içinde)",
                adapter.Source, DateTime.UtcNow.Add(delay), delay);

            try
            {
                await Task.Delay(delay, ct);
                await FetchTodayAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // INGR-007: Transient hatalar (HTTP/JSON/XML/EF transient/Polly
                // ExternalApiException) exponential backoff ile aynı gün içinde
                // 5 deneme — "scheduled time geçti, 24sa bekle" davranışı engellenir.
                // Codacy follow-up: blanket Exception catch yerine specific filter
                // — bug/config error (Null/Argument/InvalidOperation) loop'tan
                // sızar ve fail-fast olur.
                if (!await TryRecoverWithBackoffAsync(ex, ct))
                    break; // cancellation token tetiklendi
            }
        }
    }

    /// <summary>
    /// INGR-007 follow-up: Transient hatadan exponential backoff ile kurtulmayı dener.
    /// Aynı gün içinde en fazla 5 deneme yapar; her biri başarılı olursa <c>true</c>
    /// döner ve döngü normal scheduled cycle'a geri döner. Cancellation token
    /// tetiklenirse <c>false</c> döner (caller break eder).
    /// </summary>
    private async Task<bool> TryRecoverWithBackoffAsync(Exception cause, CancellationToken ct)
    {
        var backoff = TimeSpan.FromMinutes(5);
        const int maxAttempts = 5;
        // Sonar S6646: aynı block içinde 2 LogError vardı (giriş + tükenme). Giriş
        // mesajı transient hata için "henüz error değil" → LogWarning'e indirildi.
        // Asıl LogError sadece tüm denemeler tükendiğinde atılır.
        logger.LogWarning(cause,
            "{Source} günlük çekim sırasında beklenmeyen hata — exponential backoff ile {Max} deneme",
            adapter.Source, maxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return false; }

            try
            {
                await FetchTodayAsync(ct);
                logger.LogInformation(
                    "{Source} transient hatadan kurtulundu (deneme {Attempt}/{Max})",
                    adapter.Source, attempt, maxAttempts);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception retryEx)
            {
                logger.LogWarning(retryEx,
                    "{Source} retry {Attempt}/{Max} başarısız; {Backoff:hh\\:mm} bekle",
                    adapter.Source, attempt, maxAttempts, backoff);
                backoff = TimeSpan.FromTicks(backoff.Ticks * 2);
            }
        }

        logger.LogError(
            "{Source} {MaxAttempts} deneme sonrasında günlük veri çekilemedi; bir sonraki scheduled cycle'a düşülüyor",
            adapter.Source, maxAttempts);
        return true; // caller döngüye dönsün; bir sonraki cycle planla.
    }

    private bool IsScheduledTimePassedToday()
    {
        var now = DateTime.UtcNow;
        var scheduledToday = now.Date.Add(DailyRunUtcTime.ToTimeSpan());
        return now >= scheduledToday;
    }

    /// <summary>
    /// Codacy follow-up: Transient ve "expected" dış kaynak hataları beyaz-listesi.
    /// Sadece bu set retry pencereye alınır; bug (Null/Argument/InvalidOperation)
    /// ve programlama hataları rethrow edilerek fail-fast davranışı korunur.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        ExternalApiException                   => true, // dış API beklenen hata (Polly tükenmiş)
        HttpRequestException                   => true, // network/DNS, transient HTTP
        TaskCanceledException                  => true, // per-attempt timeout (non-shutdown)
        TimeoutException                       => true,
        JsonException                          => true, // adapter payload schema drift
        XmlException                           => true, // TCMB XML transient
        DbUpdateException                      => true, // EF transient (deadlock, connection blink)
        NpgsqlException                        => true, // Npgsql transient (server reset)
        _                                      => false,
    };

    /// <summary>
    /// FetchToday'ı backfill sonrası tetikleyip tetiklememeyi kararlaştırır.
    /// İki koşul: (1) bugünün scheduled saati geçmiş; (2) herhangi bir aktif
    /// asset için en son saklanan tarih TargetDate'in gerisinde — yani backfill
    /// gece yarısını aşmış ve dünün günlük çekimi atlanmış olabilir (review P1R-005).
    /// </summary>
    private async Task<bool> IsImmediateFetchNeededAsync(CancellationToken ct)
    {
        if (IsScheduledTimePassedToday())
            return true;

        var target = TargetDate(DateTime.UtcNow);
        var assets = await repository.GetActiveAssetsBySourceAsync(adapter.Source, ct);
        foreach (var asset in assets)
        {
            var latest = await repository.GetLatestPriceDateAsync(asset.Id, ct);
            if (latest is null || latest.Value < target)
                return true;
        }
        return false;
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        var assets = await repository.GetActiveAssetsBySourceAsync(adapter.Source, ct);

        foreach (var asset in assets)
        {
            var to = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));

            // F2.4-9: Gap-aware mode (yalnız 7/24 piyasalar için) tüm aralıkta var
            // olmayan tarihleri hedefler. Default davranış (latestDate sonrası tek blok)
            // hafta sonu/tatil "boş" olan kaynaklarda gereksiz API çağrısı yaratmaz.
            if (EnableGapAwareBackfill)
            {
                await BackfillGapsAsync(asset, BackfillStartDate, to, ct);
                continue;
            }

            var latestDate = await repository.GetLatestPriceDateAsync(asset.Id, ct);
            var from = latestDate?.AddDays(1) ?? BackfillStartDate;

            if (from > to)
            {
                logger.LogInformation("{Symbol} için backfill gerekmiyor (mevcut: {Latest})",
                    asset.Symbol, latestDate);
                continue;
            }

            logger.LogInformation("{Symbol} backfill başlıyor: {From} → {To}", asset.Symbol, from, to);
            await BackfillChunkedAsync(asset, from, to, ct);
        }
    }

    private async Task BackfillGapsAsync(Asset asset, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from > to) return;
        var existing = await repository.GetExistingDatesAsync(asset.Id, from, to, ct);
        var missingRanges = ComputeMissingRanges(from, to, existing);

        if (missingRanges.Count == 0)
        {
            logger.LogInformation("{Symbol} için backfill gerekmiyor (tüm aralık DB'de mevcut)", asset.Symbol);
            return;
        }

        logger.LogInformation(
            "{Symbol} gap-aware backfill: {RangeCount} eksik blok ({From}..{To})",
            asset.Symbol, missingRanges.Count, from, to);

        foreach (var (rangeFrom, rangeTo) in missingRanges)
        {
            if (ct.IsCancellationRequested) break;
            await BackfillChunkedAsync(asset, rangeFrom, rangeTo, ct);
        }
    }

    private async Task BackfillChunkedAsync(Asset asset, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var chunkFrom = from;
        while (chunkFrom <= to && !ct.IsCancellationRequested)
        {
            var chunkTo = chunkFrom.AddDays(ChunkDays - 1);
            if (chunkTo > to) chunkTo = to;

            await FetchAndUpsertAsync(asset, chunkFrom, chunkTo,
                IngestionJobTypes.HistoricalBackfill, ct);
            chunkFrom = chunkTo.AddDays(1);

            if (ChunkDelay > TimeSpan.Zero && chunkFrom <= to)
                await Task.Delay(ChunkDelay, ct);
        }
    }

    /// <summary>
    /// F2.4-9: <paramref name="existing"/> kümesi temel alınarak <paramref name="from"/> ↔
    /// <paramref name="to"/> aralığında bitişik eksik gün bloklarını döner.
    /// Açgözlü tarama, O(<c>to-from</c>) zaman; test kapsamı kolay.
    /// </summary>
    internal static List<(DateOnly From, DateOnly To)> ComputeMissingRanges(
        DateOnly from, DateOnly to, IReadOnlySet<DateOnly> existing)
    {
        var result = new List<(DateOnly, DateOnly)>();
        DateOnly? rangeStart = null;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (existing.Contains(d))
            {
                if (rangeStart.HasValue)
                {
                    result.Add((rangeStart.Value, d.AddDays(-1)));
                    rangeStart = null;
                }
            }
            else
            {
                rangeStart ??= d;
            }
        }
        if (rangeStart.HasValue)
            result.Add((rangeStart.Value, to));
        return result;
    }

    private async Task FetchTodayAsync(CancellationToken ct)
    {
        var target = TargetDate(DateTime.UtcNow);
        var assets = await repository.GetActiveAssetsBySourceAsync(adapter.Source, ct);
        foreach (var asset in assets)
            await FetchAndUpsertAsync(asset, target, target,
                IngestionJobTypes.DailyUpdate, ct);
    }

    private async Task FetchAndUpsertAsync(
        Asset asset, DateOnly from, DateOnly to, string jobType, CancellationToken ct)
    {
        // ingestion_jobs.StartAsync DB hatası fırlatırsa caller (RunAsync) içinde
        // OperationCanceledException dışındaki exception'ları yakalamadığı için
        // tüm worker düşerdi. Bu yüzden StartAsync de try kapsamında; başarısız olursa
        // log + skip ile asset döngüsü devam eder.
        IngestionJob? job = null;
        try
        {
            job = await TryStartJobAsync(asset, jobType, from, to, ct);
            if (job is null) return;

            var points = await FetchPointsAsync(asset, from, to, ct);
            await CompleteJobAsync(asset, job.Id, points, from, to, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — job'ı failed olarak işaretlemek anlamlı değil; running kalır,
            // bir sonraki başlatmada operasyon ekibi görür.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Symbol} veri çekimi başarısız ({From}–{To})", asset.Symbol, from, to);
            if (job is not null)
                await MarkFailedSafelyAsync(asset, job.Id, ex, ct);
        }
    }

    private async Task<IngestionJob?> TryStartJobAsync(
        Asset asset, string jobType, DateOnly from, DateOnly to, CancellationToken ct)
    {
        // job kaydı açılamazsa (DB flap, network) ingestion'ı atla — bir sonraki
        // cycle'da tekrar denenir. Loglanır, ama worker'ın tamamen düşmesini engeller.
        try
        {
            return await jobs.StartAsync(asset.Id, jobType, from, to, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{Symbol} ingestion_jobs.StartAsync başarısız ({From}–{To}) — ingestion atlandı",
                asset.Symbol, from, to);
            return null;
        }
    }

    private async Task<IReadOnlyList<PricePoint>> FetchPointsAsync(
        Asset asset, DateOnly from, DateOnly to, CancellationToken ct) =>
        await adapter.FetchRangeAsync(
            asset.Id, asset.Symbol, asset.SourceId ?? string.Empty, from, to, ct);

    private async Task CompleteJobAsync(
        Asset asset, Guid jobId, IReadOnlyList<PricePoint> points, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (points.Count == 0)
        {
            logger.LogInformation("{Symbol}: {From}–{To} arasında alınacak veri yok", asset.Symbol, from, to);
            await jobs.MarkSuccessAsync(jobId, recordsUpserted: 0, ct);
            return;
        }

        await repository.UpsertPricePointsAsync(points, ct);
        await jobs.MarkSuccessAsync(jobId, points.Count, ct);

        logger.LogInformation("{Symbol}: {Count} fiyat noktası kaydedildi ({From}–{To})",
            asset.Symbol, points.Count, from, to);
    }

    private async Task MarkFailedSafelyAsync(Asset asset, Guid jobId, Exception cause, CancellationToken ct)
    {
        // job tamamlama best-effort — DB de erişilemez ise ek noise yok.
        // GetBaseException(): AggregateException / TaskCanceledException sarmalayıcılarının
        // altındaki gerçek nedeni alır → ingestion_jobs.error_message daha tanı koymaya
        // elverişli olur.
        try
        {
            await jobs.MarkFailedAsync(jobId, cause.GetBaseException().Message, ct);
        }
        catch (Exception jobEx)
        {
            logger.LogError(jobEx, "{Symbol} job failed-status yazılamadı (jobId={JobId})",
                asset.Symbol, jobId);
        }
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var now = DateTime.UtcNow;
        var todayScheduled = now.Date.Add(DailyRunUtcTime.ToTimeSpan());
        return now < todayScheduled ? todayScheduled - now : todayScheduled.AddDays(1) - now;
    }
}
