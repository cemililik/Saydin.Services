using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Saydin.PriceIngestion.Workers;

namespace Saydin.PriceIngestion.Tests.Workers;

public sealed class IngestionOrchestratorTests
{
    [Fact]
    public async Task InfrastructureFatal_CancelsSibling_LogsIdentity_SetsExitOne_AndRethrows()
    {
        var siblingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fatal = new FatalTestException();
        var sink = new CaptureExitCodeSink();
        var logger = new CaptureLogger<IngestionOrchestrator>();
        var orchestrator = Create(
            [
                new("Tcmb", async _ =>
                {
                    await siblingStarted.Task;
                    throw fatal;
                }),
                new("CoinGecko", async token =>
                {
                    siblingStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    finally
                    {
                        if (token.IsCancellationRequested)
                            siblingCancelled.TrySetResult();
                    }
                }),
            ],
            Enabled("Tcmb", "CoinGecko"), sink, logger);

        var run = () => orchestrator.RunForTestAsync(CancellationToken.None);
        var failure = await run.Should().ThrowAsync<FatalTestException>();

        failure.Which.Should().BeSameAs(fatal);
        await siblingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        sink.ExitCode.Should().Be(1);
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Critical && entry.Message.Contains("Tcmb"));
    }

    [Fact]
    public async Task FatalWithNonCooperativeSibling_ThrowsAfterBoundedDrain_AndSetsExitOne()
    {
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CaptureExitCodeSink();
        var logger = new CaptureLogger<IngestionOrchestrator>();
        var configuration = Enabled("Tcmb", "CoinGecko", drainTimeoutMs: 25);
        var orchestrator = Create(
            [
                new("Tcmb", _ => Task.FromException(new FatalTestException())),
                new("CoinGecko", _ => never.Task),
            ], configuration, sink, logger);
        var stopwatch = Stopwatch.StartNew();

        var run = () => orchestrator.RunForTestAsync(CancellationToken.None);
        await run.Should().ThrowAsync<FatalTestException>();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        sink.ExitCode.Should().Be(1);
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("25"));
    }

    [Fact]
    public async Task NormalHostCancellation_CancelsWorker_AndPreservesExistingNonZeroExit()
    {
        var workerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CaptureExitCodeSink { ExitCode = 7 };
        var logger = new CaptureLogger<IngestionOrchestrator>();
        var orchestrator = Create(
            [new("Tcmb", async token =>
            {
                workerStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            })],
            Enabled("Tcmb"), sink, logger);
        using var shutdown = new CancellationTokenSource();

        var execution = orchestrator.RunForTestAsync(shutdown.Token);
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        shutdown.Cancel();
        await execution.WaitAsync(TimeSpan.FromSeconds(1));

        sink.ExitCode.Should().Be(7);
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task NoEnabledWorker_FailsStartupWithoutInvokingWorker_AndSetsExitOne()
    {
        var calls = 0;
        var sink = new CaptureExitCodeSink();
        var logger = new CaptureLogger<IngestionOrchestrator>();
        var orchestrator = Create(
            [new("Tcmb", _ =>
            {
                calls++;
                return Task.CompletedTask;
            })],
            new ConfigurationBuilder().Build(), sink, logger);

        var run = () => orchestrator.StartAsync(CancellationToken.None);
        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No ingestion workers enabled*");

        calls.Should().Be(0);
        sink.ExitCode.Should().Be(1);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task UnexpectedNormalWorkerReturn_IsFatalAndCancelsSibling()
    {
        var siblingCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CaptureExitCodeSink();
        var orchestrator = Create(
            [
                new("Tcmb", _ => Task.CompletedTask),
                new("CoinGecko", async token =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    finally
                    {
                        if (token.IsCancellationRequested)
                            siblingCancelled.TrySetResult();
                    }
                }),
            ], Enabled("Tcmb", "CoinGecko"), sink,
            new CaptureLogger<IngestionOrchestrator>());

        var run = () => orchestrator.RunForTestAsync(CancellationToken.None);
        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Tcmb*");
        await siblingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        sink.ExitCode.Should().Be(1);
    }

    private static IngestionOrchestrator Create(
        IReadOnlyList<IngestionWorkerRegistration> workers,
        IConfiguration configuration,
        IProcessExitCodeSink sink,
        ILogger<IngestionOrchestrator> logger) =>
        new(workers, configuration, sink, TimeProvider.System, logger);

    private static IConfiguration Enabled(
        string first,
        string? second = null,
        int drainTimeoutMs = 100)
    {
        var values = new Dictionary<string, string?>
        {
            [$"IngestionWorkers:{first}:Enabled"] = "true",
            ["IngestionWorkers:SupervisorDrainTimeoutMs"] = drainTimeoutMs.ToString(),
        };
        if (second is not null)
            values[$"IngestionWorkers:{second}:Enabled"] = "true";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class CaptureExitCodeSink : IProcessExitCodeSink
    {
        public int ExitCode { get; set; }
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FatalTestException : Exception;
}
