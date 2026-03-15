using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class GoldApiWorker(
    GoldApiAdapter adapter,
    IPriceIngestionRepository repository,
    ILogger<GoldApiWorker> logger)
    : BaseAssetWorker(adapter, repository, logger)
{
    protected override DateOnly BackfillStartDate => new(2010, 1, 1);
    protected override int ChunkDays => 90;
    protected override TimeOnly DailyRunUtcTime => new(18, 0, 0);
}
