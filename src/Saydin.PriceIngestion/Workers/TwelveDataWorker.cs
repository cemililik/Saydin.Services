using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class TwelveDataWorker(
    TwelveDataAdapter adapter,
    IPriceIngestionRepository repository,
    IIngestionWindowRepository windows,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<TwelveDataWorker> logger)
    : BaseAssetWorker(adapter, repository, windows, configuration, timeProvider, logger)
{
    private const int DefaultProviderDelayMinutes = 10;
    private const int MaximumProviderDelayMinutes = 120;

    protected override int ContractVersion => 2;
    // MVP: son 2 yıl. Free plan 8 istek/dakika — chunk'lar arası 8 sn bekleme.
    protected override DateOnly BackfillStartDate => new(2024, 1, 1);
    protected override int ChunkDays => 365;
    protected override string WorkerConfigKey => "TwelveData";
    protected override TimeOnly DefaultDailyRunUtcTime => new(15, 20, 0);
    protected override TimeSpan ChunkDelay => TimeSpan.FromSeconds(8);

    protected override DateOnly TargetDate(DateTime utcNow)
    {
        var delayMinutes = Configuration.GetSection("IngestionWorkers:TwelveData")
            .GetValue<int?>("ProviderSettlementDelayMinutes") ?? DefaultProviderDelayMinutes;
        if (delayMinutes is < 0 or > MaximumProviderDelayMinutes)
            throw new InvalidOperationException(
                $"TwelveData provider settlement delay 0..{MaximumProviderDelayMinutes} dakika olmalıdır.");

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var local = TimeZoneInfo.ConvertTime(
            new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)), zone);
        var cutoff = new TimeOnly(18, 10).AddMinutes(delayMinutes);
        var today = DateOnly.FromDateTime(local.DateTime);
        return TimeOnly.FromDateTime(local.DateTime) >= cutoff ? today : today.AddDays(-1);
    }

    protected override DateOnly BackfillThrough(DateTimeOffset utcNow) =>
        TargetDate(utcNow.UtcDateTime);
}
