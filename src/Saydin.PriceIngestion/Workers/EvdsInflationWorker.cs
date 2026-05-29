using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// TCMB EVDS üzerinden TÜİK TÜFE aylık endeks verisi çeken worker.
/// Başlangıçta eksik ayları 2003-01-01'den backfill eder.
/// Ardından her ayın {MonthlyRunDay}. günü saat {DailyRunUtcHour}:00 UTC'de çalışır.
/// appsettings.json → IngestionWorkers:EvdsInflation ile tüm parametreler override edilebilir.
///
/// INGR-002 (migration 012): Artık <c>ingestion_jobs</c> tablosuna kayıt yazar
/// (<c>asset_id = null</c>, <c>source = "evds"</c>, job_type <c>inflation_backfill</c> /
/// <c>inflation_daily</c>) — CLAUDE.md "ingestion_jobs'a başarı ve hata durumları yazılır"
/// kuralına uyar. Job kaydı best-effort'tur; DB hatası asıl ingestion'ı maskelemez.
///
/// F1.4-2 / [C-D-37]: Generic <c>IBaseWorker&lt;TPayload&gt;</c> abstraction'ı bilinçli
/// olarak Faz 4'e ertelendi — `BaseAssetWorker` günlük + asset_id bazlı (price_points),
/// bu worker aylık + global (inflation_rates) yazıyor; zamanlama, iterasyon ve gap-aware
/// backfill yapısal olarak farklı (bkz. PHASE-3-DOC-UPDATE-NOTES).
/// targetMonth hesaplaması ([C-D-38]): `AddMonths(-1)` yıl-rollover'ı doğru ele alır;
/// Aralık 3'ünde Kasım, Ocak 3'ünde önceki yılın Aralık verisi çekilir.
/// </summary>
public sealed class EvdsInflationWorker(
    IInflationAdapter adapter,
    IInflationIngestionRepository repository,
    IIngestionJobRepository jobs,
    IConfiguration configuration,
    ILogger<EvdsInflationWorker> logger)
{
    // 20 yıl geriye git; EVDS serisi 2003-01-01'e kadar gidiyor
    private static readonly DateOnly BackfillStartDate =
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-20));

    private const string ConfigKey = "IngestionWorkers:EvdsInflation";

    // Her ayın 3. günü saat 10:00 UTC (config ile override edilebilir)
    private int MonthlyRunDay =>
        configuration.GetValue<int?>($"{ConfigKey}:MonthlyRunDay") ?? 3;

    private TimeOnly MonthlyRunUtcTime => new(
        configuration.GetValue<int?>($"{ConfigKey}:DailyRunUtcHour")   ?? 10,
        configuration.GetValue<int?>($"{ConfigKey}:DailyRunUtcMinute") ?? 0);

    public async Task RunAsync(CancellationToken ct)
    {
        await BackfillAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun();
            logger.LogInformation(
                "EVDS TÜFE sonraki çekim: {NextRun:dd.MM.yyyy HH:mm} UTC ({Days} gün {Hours} saat içinde)",
                DateTime.UtcNow.Add(delay), (int)delay.TotalDays, delay.Hours);

            try
            {
                await Task.Delay(delay, ct);
                await FetchLatestAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        var latestDate = await repository.GetLatestInflationDateAsync(ct);

        // Bir sonraki eksik aydan başla
        var from = latestDate.HasValue
            ? latestDate.Value.AddMonths(1)
            : BackfillStartDate;

        // Şu anki ay henüz yayınlanmamış olabilir; bir önceki aya kadar al
        var to = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);

        if (from > to)
        {
            logger.LogInformation("EVDS TÜFE: backfill gerekmiyor (son kayıt: {Latest})", latestDate);
            return;
        }

        logger.LogInformation("EVDS TÜFE backfill başlıyor: {From} → {To}", from, to);
        await RunInflationJobAsync(IngestionJobTypes.InflationBackfill, from, to, ct);
    }

    private async Task FetchLatestAsync(CancellationToken ct)
    {
        // Bir önceki ayın verisini çek (TÜİK yayın gecikmesi nedeniyle)
        var targetMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        await RunInflationJobAsync(IngestionJobTypes.InflationDaily, targetMonth, targetMonth, ct);
    }

    /// <summary>
    /// INGR-002: fetch + upsert akışını <c>ingestion_jobs</c> yaşam döngüsüyle sarmalar
    /// (asset_id=null, source=adapter.Source). Job kaydı best-effort'tur — job DB hatası
    /// asıl ingestion'ı maskelemez ve worker'ı düşürmez (log-and-continue korunur).
    /// </summary>
    private async Task RunInflationJobAsync(string jobType, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var job = await TryStartJobAsync(jobType, from, to, ct);
        try
        {
            var rates = await adapter.FetchRangeAsync(from, to, ct);
            await repository.UpsertInflationRatesAsync(rates, ct);
            await TryMarkSuccessAsync(job, rates.Count, ct);
            logger.LogInformation(
                "EVDS TÜFE {JobType} tamamlandı: {From} → {To} ({Count} kayıt)",
                jobType, from, to, rates.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await TryMarkFailedAsync(job, ex, ct);
            logger.LogError(ex, "EVDS TÜFE {JobType} başarısız ({From}–{To})", jobType, from, to);
        }
    }

    private async Task<IngestionJob?> TryStartJobAsync(
        string jobType, DateOnly from, DateOnly to, CancellationToken ct)
    {
        try
        {
            // asset_id=null: inflation bir asset değil (INGR-002). source: provenance.
            return await jobs.StartAsync(assetId: null, jobType, from, to, adapter.Source, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EVDS ingestion_jobs.StartAsync başarısız ({JobType}) — job kaydı atlanıyor", jobType);
            return null;
        }
    }

    private async Task TryMarkSuccessAsync(IngestionJob? job, int recordsUpserted, CancellationToken ct)
    {
        if (job is null) return;
        try
        {
            await jobs.MarkSuccessAsync(job.Id, recordsUpserted, ct);
        }
        catch (Exception ex)
        {
            // Best-effort telemetri — job güncellemesi başarısız olsa da ingestion başarılı sayılır.
            logger.LogWarning(ex, "EVDS ingestion_jobs MarkSuccess başarısız: {JobId}", job.Id);
        }
    }

    private async Task TryMarkFailedAsync(IngestionJob? job, Exception cause, CancellationToken ct)
    {
        if (job is null) return;
        try
        {
            await jobs.MarkFailedAsync(job.Id, cause.GetBaseException().Message, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EVDS ingestion_jobs MarkFailed başarısız: {JobId}", job.Id);
        }
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var now     = DateTime.UtcNow;
        var runTime = MonthlyRunUtcTime;
        var runDay  = Math.Min(MonthlyRunDay, DateTime.DaysInMonth(now.Year, now.Month));

        var thisMonthRun = new DateTime(now.Year, now.Month, runDay,
            runTime.Hour, runTime.Minute, 0, DateTimeKind.Utc);

        if (now < thisMonthRun)
            return thisMonthRun - now;

        // Sonraki ay için de clamp uygula
        var nextMonth    = now.Month == 12 ? 1 : now.Month + 1;
        var nextYear     = now.Month == 12 ? now.Year + 1 : now.Year;
        var nextRunDay   = Math.Min(MonthlyRunDay, DateTime.DaysInMonth(nextYear, nextMonth));
        var nextMonthRun = new DateTime(nextYear, nextMonth, nextRunDay,
            runTime.Hour, runTime.Minute, 0, DateTimeKind.Utc);

        return nextMonthRun - now;
    }
}
