using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class OpenExchangeRatesWorker(
    OpenExchangeRatesAdapter adapter,
    IPriceIngestionRepository repository,
    IIngestionWindowRepository windows,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<OpenExchangeRatesWorker> logger)
    : BaseAssetWorker(adapter, repository, windows, configuration, timeProvider, logger)
{
    // Free plan: 1.000 istek/ay.
    // Backfill total: ~365 gün × 2 metal = 730 adapter reads (shared day cache
    // sayesinde ~365 HTTP isteği). Ninety-day durable windows keep one cold
    // provider call inside the three-minute window deadline and limit restart loss.
    // Günlük güncelleme: 1 istek/gün (cache her ikisini karşılar).
    protected override DateOnly BackfillStartDate => new(2025, 1, 1);
    protected override int ChunkDays => 90;
    protected override string WorkerConfigKey => "OpenExchangeRates";

    // Piyasalar kapandıktan sonra (22:00 UTC)
    protected override TimeOnly DefaultDailyRunUtcTime => new(22, 0, 0);

    // OXR historical günlük snapshot'ı yalnız tamamlanmış UTC gün için terminaldir.
    protected override DateOnly TargetDate(DateTime utcNow) =>
        DateOnly.FromDateTime(utcNow.Date.AddDays(-1));
}
