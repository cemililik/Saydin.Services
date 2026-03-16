using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class OpenExchangeRatesWorker(
    OpenExchangeRatesAdapter adapter,
    IPriceIngestionRepository repository,
    ILogger<OpenExchangeRatesWorker> logger)
    : BaseAssetWorker(adapter, repository, logger)
{
    // Free plan: 1.000 istek/ay.
    // Backfill: ~365 gün × 2 metal = 730 istek (cache sayesinde yarıya iner → ~365 HTTP isteği).
    // Günlük güncelleme: 1 istek/gün (cache her ikisini karşılar).
    protected override DateOnly BackfillStartDate =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1));
    protected override int ChunkDays => 365;

    // Piyasalar kapandıktan sonra (22:00 UTC)
    protected override TimeOnly DailyRunUtcTime => new(22, 0, 0);
}
