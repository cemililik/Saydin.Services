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
                tasks.Add(RunSafelyAsync(key, runAsync, stoppingToken));
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
        // F1.1-8: Task.WhenAll fail-fast değil — her worker kendi RunSafelyAsync sarmalayıcısı
        // içinde exception'ı yakalar; bir worker çökerse diğerleri çalışmaya devam eder.
        await Task.WhenAll(tasks);
    }

    private async Task RunSafelyAsync(string workerName, Func<Task> runAsync, CancellationToken stoppingToken)
    {
        try
        {
            await runAsync();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown — beklenen, log gürültüsü yapma.
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Worker fatal hata: {Worker} — orchestrator izolasyonu devreye girdi (diğer worker'lar devam ediyor)",
                workerName);
        }
    }

    public override Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IngestionOrchestrator durduruluyor");
        return base.StopAsync(stoppingToken);
    }
}
