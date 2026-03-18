using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class TwelveDataWorker(
    TwelveDataAdapter adapter,
    IPriceIngestionRepository repository,
    IConfiguration configuration,
    ILogger<TwelveDataWorker> logger)
    : BaseAssetWorker(adapter, repository, configuration, logger)
{
    // MVP: son 2 yıl. Free plan 8 istek/dakika — chunk'lar arası 8 sn bekleme.
    protected override DateOnly BackfillStartDate =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-2));
    protected override int ChunkDays => 365;
    protected override string WorkerConfigKey => "TwelveData";
    protected override TimeOnly DefaultDailyRunUtcTime => new(15, 0, 0);
    protected override TimeSpan ChunkDelay => TimeSpan.FromSeconds(8);
}
