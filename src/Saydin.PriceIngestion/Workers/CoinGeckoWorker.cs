using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class CoinGeckoWorker(
    CoinGeckoAdapter adapter,
    IPriceIngestionRepository repository,
    ILogger<CoinGeckoWorker> logger)
    : BaseAssetWorker(adapter, repository, logger)
{
    protected override DateOnly BackfillStartDate => new(2017, 1, 1);
    protected override int ChunkDays => 365;
    protected override TimeOnly DailyRunUtcTime => new(2, 0, 0);
}
