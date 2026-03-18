using Microsoft.Extensions.Configuration;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Tüm veri çekme worker'larını başlatan ana orchestrator.
/// Hangi worker'ların çalışacağı appsettings.json → IngestionWorkers:{Worker}:Enabled ile belirlenir.
/// Varsayılan: tümü aktif.
/// </summary>
public sealed class IngestionOrchestrator(
    TcmbWorker tcmbWorker,
    CoinGeckoWorker coinGeckoWorker,
    // GoldApiWorker goldApiWorker,  // Pasif: OpenExchangeRates ile değiştirildi
    OpenExchangeRatesWorker openExchangeRatesWorker,
    TwelveDataWorker twelveDataWorker,
    EvdsInflationWorker evdsInflationWorker,
    IConfiguration configuration,
    ILogger<IngestionOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();

        void AddIfEnabled(string key, Func<Task> runAsync)
        {
            var enabled = configuration.GetValue<bool?>($"IngestionWorkers:{key}:Enabled") ?? true;
            if (enabled)
                tasks.Add(runAsync());
            else
                logger.LogInformation("Worker devre dışı (config): {Worker}", key);
        }

        AddIfEnabled("Tcmb",              () => tcmbWorker.RunAsync(stoppingToken));
        AddIfEnabled("CoinGecko",         () => coinGeckoWorker.RunAsync(stoppingToken));
        AddIfEnabled("OpenExchangeRates", () => openExchangeRatesWorker.RunAsync(stoppingToken));
        AddIfEnabled("TwelveData",        () => twelveDataWorker.RunAsync(stoppingToken));
        AddIfEnabled("EvdsInflation",     () => evdsInflationWorker.RunAsync(stoppingToken));

        if (tasks.Count == 0)
        {
            logger.LogWarning("Hiçbir worker aktif değil. IngestionWorkers config'ini kontrol et.");
            return;
        }

        logger.LogInformation("IngestionOrchestrator başlatıldı ({Count} aktif worker)", tasks.Count);
        await Task.WhenAll(tasks);
    }

    public override Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator durduruluyor");
        return base.StopAsync(stoppingToken);
    }
}
