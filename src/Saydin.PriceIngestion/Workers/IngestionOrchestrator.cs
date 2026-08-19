using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Configuration;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Enabled ingestion workers share one fatal-failure domain. The first fatal,
/// permanent, self-cancellation or unexpected normal return cancels every sibling,
/// performs a bounded drain, marks the process unsuccessful and is rethrown to the
/// generic host. Normal host cancellation remains a quiet, zero-exit path.
/// </summary>
public sealed class IngestionOrchestrator : BackgroundService
{
    private const int DefaultDrainTimeoutMs = 5_000;
    private const int MaximumDrainTimeoutMs = 30_000;

    private readonly IReadOnlyList<IngestionWorkerRegistration> _workers;
    private readonly IConfiguration _configuration;
    private readonly IProcessExitCodeSink _exitCode;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IngestionOrchestrator> _logger;
    private readonly TimeSpan _drainTimeout;

    public IngestionOrchestrator(
        TcmbWorker tcmbWorker,
        CoinGeckoWorker coinGeckoWorker,
        OpenExchangeRatesWorker openExchangeRatesWorker,
        TwelveDataWorker twelveDataWorker,
        EvdsInflationWorker evdsInflationWorker,
        IConfiguration configuration,
        IProcessExitCodeSink exitCode,
        TimeProvider timeProvider,
        ILogger<IngestionOrchestrator> logger)
        : this(
            [
                new("Tcmb", token => tcmbWorker.RunAsync(token)),
                new("CoinGecko", token => coinGeckoWorker.RunAsync(token)),
                new("OpenExchangeRates", token => openExchangeRatesWorker.RunAsync(token)),
                new("TwelveData", token => twelveDataWorker.RunAsync(token)),
                new("EvdsInflation", token => evdsInflationWorker.RunAsync(token)),
            ],
            configuration,
            exitCode,
            timeProvider,
            logger)
    {
    }

    internal IngestionOrchestrator(
        IReadOnlyList<IngestionWorkerRegistration> workers,
        IConfiguration configuration,
        IProcessExitCodeSink exitCode,
        TimeProvider timeProvider,
        ILogger<IngestionOrchestrator> logger)
    {
        _workers = workers;
        _configuration = configuration;
        _exitCode = exitCode;
        _timeProvider = timeProvider;
        _logger = logger;
        var configuredTimeout = configuration.GetValue<int?>(
            "IngestionWorkers:SupervisorDrainTimeoutMs") ?? DefaultDrainTimeoutMs;
        _drainTimeout = TimeSpan.FromMilliseconds(
            Math.Clamp(configuredTimeout, 1, MaximumDrainTimeoutMs));
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // .NET 10 runs BackgroundService.ExecuteAsync fully on a background thread.
        // Validate synchronously here so a zero-worker deployment fails host startup
        // instead of briefly reporting "Application started" before stopping.
        if (!_workers.Any(IsEnabled))
            throw NoEnabledWorkerException();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = new List<IngestionWorkerRegistration>();
        foreach (var worker in _workers)
        {
            if (IsEnabled(worker))
                enabled.Add(worker);
            else
                _logger.LogInformation("Worker devre dışı (config): {Worker}", worker.Name);
        }

        if (enabled.Count == 0)
            throw NoEnabledWorkerException();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var running = enabled
            .Select(worker => new RunningWorker(
                worker.Name, InvokeWorkerAsync(worker, linkedCts.Token)))
            .ToArray();
        var shutdownSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdownRegistration = stoppingToken.Register(
            () => shutdownSignal.TrySetResult());

        _logger.LogInformation(
            "IngestionOrchestrator başlatıldı ({Count} aktif worker)", running.Length);

        var completedTask = await Task.WhenAny(
            running.Select(worker => worker.Task).Append(shutdownSignal.Task));
        if (ReferenceEquals(completedTask, shutdownSignal.Task)
            || (stoppingToken.IsCancellationRequested && !completedTask.IsFaulted))
        {
            linkedCts.Cancel();
            await DrainAsync(running, fatalWorker: null);
            _exitCode.ExitCode = 0;
            return;
        }

        var completedWorker = running.Single(worker => ReferenceEquals(worker.Task, completedTask));
        Exception fatal;
        try
        {
            await completedWorker.Task;
            fatal = new InvalidOperationException(
                $"Ingestion worker terminated unexpectedly: {completedWorker.Name}");
        }
        catch (Exception ex)
        {
            fatal = ex;
        }

        _exitCode.ExitCode = 1;
        _logger.LogCritical(fatal,
            "Worker fatal hata: {Worker}; sibling worker'lar iptal ediliyor",
            completedWorker.Name);
        linkedCts.Cancel();
        await DrainAsync(running, completedWorker.Name);

        ExceptionDispatchInfo.Capture(fatal).Throw();
    }

    internal Task RunForTestAsync(CancellationToken stoppingToken) =>
        ExecuteAsync(stoppingToken);

    private bool IsEnabled(IngestionWorkerRegistration worker) =>
        _configuration.GetValue<bool?>(
            $"IngestionWorkers:{worker.Name}:Enabled") ?? false;

    private InvalidOperationException NoEnabledWorkerException()
    {
        _exitCode.ExitCode = 1;
        _logger.LogCritical(
            "Hiçbir ingestion worker etkin değil — orchestrator başlatılamıyor. " +
            "En az bir worker'ı 'IngestionWorkers:*:Enabled' ile etkinleştir.");
        return new InvalidOperationException(
            "No ingestion workers enabled; check IngestionWorkers:*:Enabled configuration.");
    }

    private static async Task InvokeWorkerAsync(
        IngestionWorkerRegistration worker,
        CancellationToken token) =>
        await worker.RunAsync(token).ConfigureAwait(false);

    private async Task DrainAsync(
        IReadOnlyList<RunningWorker> workers,
        string? fatalWorker)
    {
        var observation = Task.WhenAll(workers.Select(ObserveTerminationAsync));
        var timeout = Task.Delay(_drainTimeout, _timeProvider, CancellationToken.None);
        if (await Task.WhenAny(observation, timeout) == observation)
        {
            await observation;
            return;
        }

        var pending = workers.Count(worker => !worker.Task.IsCompleted);
        _logger.LogWarning(
            "Worker drain süresi aşıldı: {TimeoutMs}ms, fatal={FatalWorker}, pending={PendingCount}",
            _drainTimeout.TotalMilliseconds, fatalWorker, pending);
        _ = observation; // Each worker task remains observed even after bounded return.
    }

    private static async Task ObserveTerminationAsync(RunningWorker worker)
    {
        try
        {
            await worker.Task.ConfigureAwait(false);
        }
        catch
        {
            // Fatal is propagated by ExecuteAsync; sibling cancellation/faults are
            // observed here so bounded drain cannot create unobserved exceptions.
        }
    }

    public override Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IngestionOrchestrator durduruluyor");
        return base.StopAsync(stoppingToken);
    }

    private sealed record RunningWorker(string Name, Task Task);
}

internal sealed record IngestionWorkerRegistration(
    string Name,
    Func<CancellationToken, Task> RunAsync);
