using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class CoinGeckoWorker(
    CoinGeckoAdapter adapter,
    IPriceIngestionRepository repository,
    ILogger<CoinGeckoWorker> logger)
    : BaseAssetWorker(adapter, repository, logger)
{
    // MVP: son 2 yıl. Demo key yalnızca ~365 güne erişim sağlıyor.
    protected override DateOnly BackfillStartDate =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-2));
    protected override int ChunkDays => 365;
    protected override TimeOnly DailyRunUtcTime => new(2, 0, 0);
}
