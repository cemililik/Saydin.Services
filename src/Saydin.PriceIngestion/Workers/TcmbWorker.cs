using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// TCMB döviz kurlarını çeken worker.
/// Başlangıçta eksik günleri backfill eder, ardından her gün 16:30 Türkiye saatinde (13:30 UTC) çalışır.
/// </summary>
public sealed class TcmbWorker(
    TcmbAdapter adapter,
    IPriceIngestionRepository repository,
    IConfiguration configuration,
    ILogger<TcmbWorker> logger)
    : BaseAssetWorker(adapter, repository, configuration, logger)
{
    // Son 20 yıl (TCMB arşivi çok daha eskilere gidiyor)
    protected override DateOnly BackfillStartDate =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-20));
    protected override int ChunkDays => 90;
    protected override string WorkerConfigKey => "Tcmb";

    // 16:30 Türkiye = 13:30 UTC (Türkiye UTC+3, DST kullanmıyor — 2016'dan beri)
    protected override TimeOnly DefaultDailyRunUtcTime => new(13, 30, 0);
}
