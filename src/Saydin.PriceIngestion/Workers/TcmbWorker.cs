using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// TCMB döviz kurlarını çeken worker.
/// Başlangıçta eksik günleri backfill eder, ardından her gün 16:30 Türkiye saatinde (13:30 UTC) çalışır.
/// </summary>
public sealed class TcmbWorker(
    TcmbAdapter adapter,
    IPriceIngestionRepository repository,
    IIngestionWindowRepository windows,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<TcmbWorker> logger)
    : BaseAssetWorker(adapter, repository, windows, configuration, timeProvider, logger)
{
    protected override int ContractVersion => 2;
    protected override DateOnly BackfillStartDate => new(2006, 1, 1);
    protected override int ChunkDays => 90;
    protected override string WorkerConfigKey => "Tcmb";

    // 16:30 Türkiye = 13:30 UTC (Türkiye UTC+3, DST kullanmıyor — 2016'dan beri)
    protected override TimeOnly DefaultDailyRunUtcTime => new(13, 30, 0);

    // TCMB policy snapshot states that same-day indicative rates are published
    // after checks between 16:00 and 16:30 Europe/Istanbul. The database target
    // is still selected from the active, sealed authoritative calendar.
    protected override DateOnly TargetDate(DateTime utcNow) =>
        ProviderCutoff(new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)));

    protected override DateOnly BackfillThrough(DateTimeOffset utcNow) =>
        ProviderCutoff(utcNow);

    protected override Task<MarketCalendarTargetResolution> ResolveBackfillThroughAsync(
        DateTimeOffset utcNow,
        CancellationToken ct) =>
        Windows.ResolveLatestExpectedObservationAsync(
            CalendarDataGeneratorCode, ProviderCutoff(utcNow), ct);

    private const string CalendarDataGeneratorCode = "tcmb_indicative_fx";

    private static DateOnly ProviderCutoff(DateTimeOffset utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        return TimeOnly.FromDateTime(local.DateTime) >= new TimeOnly(16, 30)
            ? today
            : today.AddDays(-1);
    }

}
