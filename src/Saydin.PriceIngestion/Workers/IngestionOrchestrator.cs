namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Tüm veri çekme worker'larını başlatan ana orchestrator.
/// Her worker kendi zamanlama döngüsünü yönetir.
/// </summary>
public sealed class IngestionOrchestrator(
    TcmbWorker tcmbWorker,
    CoinGeckoWorker coinGeckoWorker,
    // GoldApiWorker goldApiWorker,  // Pasif: OpenExchangeRates ile değiştirildi
    OpenExchangeRatesWorker openExchangeRatesWorker,
    TwelveDataWorker twelveDataWorker,
    ILogger<IngestionOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator başlatıldı");

        await Task.WhenAll(
            tcmbWorker.RunAsync(stoppingToken),
            coinGeckoWorker.RunAsync(stoppingToken),
            openExchangeRatesWorker.RunAsync(stoppingToken),
            // goldApiWorker.RunAsync(stoppingToken),  // Pasif
            twelveDataWorker.RunAsync(stoppingToken));
    }

    public override Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator durduruluyor");
        return base.StopAsync(stoppingToken);
    }
}
