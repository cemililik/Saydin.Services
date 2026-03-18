using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// TCMB EVDS üzerinden TÜİK TÜFE aylık endeks verisi çeken worker.
/// Başlangıçta eksik ayları 2003-01-01'den backfill eder.
/// Ardından her ayın {MonthlyRunDay}. günü saat {DailyRunUtcHour}:00 UTC'de çalışır.
/// appsettings.json → IngestionWorkers:EvdsInflation ile tüm parametreler override edilebilir.
/// </summary>
public sealed class EvdsInflationWorker(
    EvdsInflationAdapter adapter,
    IInflationIngestionRepository repository,
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

        try
        {
            var rates = await adapter.FetchRangeAsync(from, to, ct);
            await repository.UpsertInflationRatesAsync(rates, ct);
            logger.LogInformation("EVDS TÜFE backfill tamamlandı: {Count} ay kaydedildi", rates.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "EVDS TÜFE backfill başarısız ({From}–{To})", from, to);
        }
    }

    private async Task FetchLatestAsync(CancellationToken ct)
    {
        // Bir önceki ayın verisini çek (TÜİK yayın gecikmesi nedeniyle)
        var targetMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);

        try
        {
            var rates = await adapter.FetchRangeAsync(targetMonth, targetMonth, ct);
            await repository.UpsertInflationRatesAsync(rates, ct);
            logger.LogInformation(
                "EVDS TÜFE aylık güncelleme: {Month:yyyy-MM} için {Count} kayıt",
                targetMonth, rates.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "EVDS TÜFE aylık güncelleme başarısız ({Month})", targetMonth);
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
