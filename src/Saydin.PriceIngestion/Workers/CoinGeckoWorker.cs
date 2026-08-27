using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class CoinGeckoWorker(
    CoinGeckoAdapter adapter,
    IPriceIngestionRepository repository,
    IIngestionWindowRepository windows,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<CoinGeckoWorker> logger)
    : BaseAssetWorker(adapter, repository, windows, configuration, timeProvider, logger)
{
    protected override DateOnly BackfillStartDate => new(2024, 1, 1);
    protected override int ChunkDays => 365;
    protected override string WorkerConfigKey => "CoinGecko";
    protected override TimeOnly DefaultDailyRunUtcTime => new(2, 0, 0);

    // F1.1-10: CoinGecko UTC günü kapandıktan sonra (02:00 UTC çekim) "today" daha
    // hiç başlamamış olabilir → dün kapanışını al. Aksi halde bugünün ilk birkaç
    // dakikasının partial verisi yanlış close olarak saklanır.
    protected override DateOnly TargetDate(DateTime utcNow) =>
        DateOnly.FromDateTime(utcNow.Date.AddDays(-1));

}
