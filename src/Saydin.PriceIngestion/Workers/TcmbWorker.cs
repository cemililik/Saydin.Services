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
    ILogger<TcmbWorker> logger)
    : BaseAssetWorker(adapter, repository, logger)
{
    // MVP için 2010 yeterli; daha eskisine ihtiyaç duyulursa migration ile düşürülür
    protected override DateOnly BackfillStartDate => new(2010, 1, 1);
    protected override int ChunkDays => 90;

    // 16:30 Türkiye = 13:30 UTC (Türkiye UTC+3, DST kullanmıyor — 2016'dan beri)
    protected override TimeOnly DailyRunUtcTime => new(13, 30, 0);
}
