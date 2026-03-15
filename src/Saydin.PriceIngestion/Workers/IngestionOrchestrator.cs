namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Tüm veri çekme worker'larını zamanlayan ana orchestrator.
/// Her veri kaynağı için ayrı bir Timer ile çalışır.
/// Başlangıçta eksik veri (gap) tespiti yaparak backfill başlatır.
/// </summary>
public sealed class IngestionOrchestrator(ILogger<IngestionOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator başlatıldı");

        // TODO: Faz 1 — Her adapter için zamanlanmış görev ekle:
        // - TcmbWorker    → 16:30 Türkiye saati (piyasa kapanış sonrası)
        // - CoinGeckoWorker → 06:00 UTC
        // - GoldApiWorker  → 07:00 UTC
        // - TwelveDataWorker → 19:00 Türkiye saati (BIST kapanış sonrası)

        // TODO: Başlangıçta gap tespiti:
        // SELECT asset_id, MAX(price_date) FROM price_points GROUP BY asset_id
        // Eksik günler için backfill tetikle

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator durduruluyor");
        return base.StopAsync(stoppingToken);
    }
}
