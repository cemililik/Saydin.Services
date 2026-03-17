using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class CoinGeckoWorker(
    CoinGeckoAdapter adapter,
    IPriceIngestionRepository repository,
    IConfiguration configuration,
    ILogger<CoinGeckoWorker> logger)
    : BaseAssetWorker(adapter, repository, configuration, logger)
{
    // MVP: son 2 yıl. Demo key yalnızca ~365 güne erişim sağlıyor.
    protected override DateOnly BackfillStartDate =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-2));
    protected override int ChunkDays => 365;
    protected override string WorkerConfigKey => "CoinGecko";
    protected override TimeOnly DefaultDailyRunUtcTime => new(2, 0, 0);
}
