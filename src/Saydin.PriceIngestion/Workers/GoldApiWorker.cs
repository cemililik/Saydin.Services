using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class GoldApiWorker(
    GoldApiAdapter adapter,
    IPriceIngestionRepository repository,
    IConfiguration configuration,
    ILogger<GoldApiWorker> logger)
    : BaseAssetWorker(adapter, repository, configuration, logger)
{
    // Free plan: 100 istek/ay, 2018+ tarihi veri. Son 1 yıl backfill yeterli.
    // Her gün 2 istek (XAU + XAG) → ayda ~60 istek, limit altında.
    protected override DateOnly BackfillStartDate =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1));
    protected override int ChunkDays => 90;
    protected override string WorkerConfigKey => "GoldApi";
    // Günlük 100 istek limiti için chunk'lar arası 1 sn bekleme
    protected override TimeSpan ChunkDelay => TimeSpan.FromSeconds(1);
    protected override TimeOnly DefaultDailyRunUtcTime => new(18, 0, 0);
}
