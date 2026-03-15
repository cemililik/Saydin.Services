namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Tüm veri çekme worker'larını başlatan ana orchestrator.
/// Her worker kendi zamanlama döngüsünü yönetir.
/// </summary>
public sealed class IngestionOrchestrator(
    TcmbWorker tcmbWorker,
    ILogger<IngestionOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator başlatıldı");

        // TODO Faz 1: CoinGeckoWorker, GoldApiWorker, TwelveDataWorker paralel olarak eklenir
        // await Task.WhenAll(
        //     tcmbWorker.RunAsync(stoppingToken),
        //     coinGeckoWorker.RunAsync(stoppingToken),
        //     ...);

        await tcmbWorker.RunAsync(stoppingToken);
    }

    public override Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator durduruluyor");
        return base.StopAsync(stoppingToken);
    }
}
