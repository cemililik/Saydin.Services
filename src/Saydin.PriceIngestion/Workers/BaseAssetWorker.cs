using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Entities;

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

    public async Task RunAsync(CancellationToken ct)
    {
        await BackfillAsync(ct);

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
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        var assets = await repository.GetActiveAssetsBySourceAsync(adapter.Source, ct);

        foreach (var asset in assets)
        {
            var latestDate = await repository.GetLatestPriceDateAsync(asset.Id, ct);
            var effectiveStart = BackfillStartDate;
            var from = latestDate?.AddDays(1) ?? effectiveStart;
            var to = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));

            if (from > to)
            {
                logger.LogInformation("{Symbol} için backfill gerekmiyor (mevcut: {Latest})",
                    asset.Symbol, latestDate);
                continue;
            }

            logger.LogInformation("{Symbol} backfill başlıyor: {From} → {To}", asset.Symbol, from, to);

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
    }

    private async Task FetchTodayAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var assets = await repository.GetActiveAssetsBySourceAsync(adapter.Source, ct);
        foreach (var asset in assets)
            await FetchAndUpsertAsync(asset, today, today,
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
        // job tamamlama best-effort — DB de erişilemez ise ek noise yok
        try
        {
            await jobs.MarkFailedAsync(jobId, cause.Message, ct);
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
