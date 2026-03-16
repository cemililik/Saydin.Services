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
    // MVP: son 2 yıl yeterli
    protected override DateOnly BackfillStartDate =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-2));
    protected override int ChunkDays => 90;

    // 16:30 Türkiye = 13:30 UTC (Türkiye UTC+3, DST kullanmıyor — 2016'dan beri)
    protected override TimeOnly DailyRunUtcTime => new(13, 30, 0);
}
