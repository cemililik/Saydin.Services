namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Tüm veri çekme worker'larını başlatan ana orchestrator.
/// Her worker kendi zamanlama döngüsünü yönetir.
/// </summary>
public sealed class IngestionOrchestrator(
    TcmbWorker tcmbWorker,
    CoinGeckoWorker coinGeckoWorker,
    GoldApiWorker goldApiWorker,
    TwelveDataWorker twelveDataWorker,
    ILogger<IngestionOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator başlatıldı");

        await Task.WhenAll(
            tcmbWorker.RunAsync(stoppingToken),
            coinGeckoWorker.RunAsync(stoppingToken),
            goldApiWorker.RunAsync(stoppingToken),
            twelveDataWorker.RunAsync(stoppingToken));
    }

    public override Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator durduruluyor");
        return base.StopAsync(stoppingToken);
    }
}
