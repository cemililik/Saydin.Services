using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class TwelveDataWorker(
    TwelveDataAdapter adapter,
    IPriceIngestionRepository repository,
    ILogger<TwelveDataWorker> logger)
    : BaseAssetWorker(adapter, repository, logger)
{
    protected override DateOnly BackfillStartDate => new(2010, 1, 1);
    protected override int ChunkDays => 365;
    protected override TimeOnly DailyRunUtcTime => new(15, 0, 0);
}
