using Microsoft.Extensions.Configuration;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Tüm veri çekme worker'larını başlatan ana orchestrator.
/// Hangi worker'ların çalışacağı <c>IngestionWorkers:{Worker}:Enabled</c> ile belirlenir.
/// F4-11: Varsayılan <b>kapalı</b> (disabled-by-default) — config'te anahtar yoksa worker
/// çalışmaz (<c>?? false</c>). Aktivasyon env (<c>WORKER_*_ENABLED</c> → compose/.env) ya da
/// appsettings ile açıkça yapılır; böylece bare-binary çalıştırma kazara dış API/rate-limit
/// tüketmez (fail-closed). Eksik/typo'lu config sessizce worker'ı açmak yerine kapalı bırakır.
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
            // F4-11: anahtar yoksa fail-closed (?? false) — worker kapalı kalır, loglanır.
            var enabled = configuration.GetValue<bool?>($"IngestionWorkers:{key}:Enabled") ?? false;
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

        // PR #11 follow-up: Eğer Task.WhenAll shutdown token tetiklenmeden tamamlandıysa,
        // tüm worker'lar sessizce ölmüş demektir (örn. RunSafelyAsync catch'i fatal hata
        // yutmuş). Hosted service'i fail ettir — host process restart almalı, "running"
        // gibi görünmemeli. CLAUDE.md "exception sessizce yutulmaz" prensibinin
        // orchestrator seviyesindeki uzantısı.
        if (!stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical(
                "Tüm ingestion worker'ları beklenmedik şekilde sonlandı; orchestrator hosted service fail ettiriyor");
            throw new InvalidOperationException(
                "All ingestion workers terminated unexpectedly while shutdown token was not signalled.");
        }
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
