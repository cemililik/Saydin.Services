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

    protected override DateOnly TargetDate(DateTime utcNow) =>
        IstanbulDate(new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc))).AddDays(-1);

    protected override DateOnly BackfillThrough(DateTimeOffset utcNow) =>
        IstanbulDate(utcNow).AddDays(-1);

    private static DateOnly IstanbulDate(DateTimeOffset utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
    }
}
