using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Saydin.Api.Tests.Helpers;

/// <summary>
/// F2.6-15 / TSTR: Test log sink. Önceden testler <c>NullLogger&lt;T&gt;</c> kullanıyordu,
/// bu yüzden "Redis down → warning loglanır", "non-positive fiyat → data bug warning"
/// gibi log davranışları doğrulanamıyordu (review C-Çapraz-C). Bu sink, üretilen log
/// kayıtlarını yakalar; testler seviye/mesaj üzerinde assertion yapabilir.
///
/// Kullanım:
/// <code>
/// var logger = new TestLogger&lt;MyService&gt;();
/// var sut = new MyService(..., logger);
/// // ...
/// logger.Entries.Should().Contain(e =&gt; e.Level == LogLevel.Warning &amp;&amp; e.Message.Contains("..."));
/// </code>
/// </summary>
public sealed class TestLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    /// <summary>Yakalanan tek bir log kaydı.</summary>
    public sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
