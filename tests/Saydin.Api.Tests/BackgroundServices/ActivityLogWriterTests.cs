using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Saydin.Api.BackgroundServices;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.BackgroundServices;

[Collection(MetricsTestCollection.Name)]
public sealed class ActivityLogWriterTests
{
    [Theory]
    [InlineData("22003", "ToxicRow")]
    [InlineData("23514", "ToxicRow")]
    [InlineData("23503", "ToxicRow")]
    [InlineData("23505", "ToxicRow")]
    [InlineData("40001", "TransientBatch")]
    [InlineData("40P01", "TransientBatch")]
    [InlineData("08006", "TransientBatch")]
    [InlineData("53300", "TransientBatch")]
    [InlineData("53200", "TransientBatch")]
    [InlineData("57P01", "TransientBatch")]
    [InlineData("57P03", "TransientBatch")]
    [InlineData("55P03", "TransientBatch")]
    [InlineData("25P02", "TransientBatch")]
    [InlineData("42P01", "FatalHost")]
    [InlineData("42501", "FatalHost")]
    [InlineData("3D000", "FatalHost")]
    [InlineData("3F000", "FatalHost")]
    [InlineData("28P01", "FatalHost")]
    [InlineData("25006", "TransientBatch")]
    [InlineData("55006", "TransientBatch")]
    [InlineData("0A000", "TransientBatch")]
    [InlineData("58030", "TransientBatch")]
    [InlineData("XX000", "TransientBatch")]
    public void Classifier_UsesExactSqlStateClasses(
        string sqlState, string expected)
    {
        var exception = new DbUpdateException("write failed", Postgres(sqlState));
        ActivityLogWriteFailureClassifier.Classify(exception).ToString().Should().Be(expected);
    }

    [Fact]
    public void Classifier_TreatsUnknownNonPostgresFailureAsTransientBatch()
    {
        ActivityLogWriteFailureClassifier.Classify(
            new InvalidOperationException("unexpected writer failure"))
            .Should().Be(ActivityLogWriteFailureKind.TransientBatch);
    }

    [Fact]
    public async Task ToxicRow_IsBisected_GoodRowsSurvive_AndMetricNamesOneDrop()
    {
        using var metrics = new FailureMetricRecorder();
        var store = new ToxicStore();
        var writer = Writer(store, [Entry("good-a"), Entry("toxic"), Entry("good-b")]);

        await RunToCompletionAsync(writer);

        store.Saved.Select(entry => entry.DeviceId).Should().Equal("good-a", "good-b");
        store.Calls.Should().BeGreaterThan(1);
        metrics.Measurements.Should().ContainSingle(item =>
            item.Value == 1 && item.Outcome == "toxic_row");
    }

    [Fact]
    public async Task SerializationFailure_RetriesWholeBatch_WithoutBisection()
    {
        var store = new RetryTwiceStore("40001");
        var entries = new[] { Entry("one"), Entry("two") };
        var writer = Writer(store, entries);

        await RunToCompletionAsync(writer);

        store.Calls.Should().Be(3);
        store.BatchSizes.Should().Equal(2, 2, 2);
        store.Saved.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task PostgreSqlRestart_RetriesWholeBatch_AndWriterSurvives()
    {
        var store = new RetryTwiceStore("57P01");
        var entries = new[] { Entry("one"), Entry("two") };
        var writer = Writer(store, entries);

        await RunToCompletionAsync(writer);

        store.Calls.Should().Be(3);
        store.BatchSizes.Should().Equal(2, 2, 2);
        store.Saved.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task TooManyConnections_ExhaustsBoundedRetry_DropsExactBatch_AndProcessesLaterWork()
    {
        using var metrics = new FailureMetricRecorder();
        var store = new ExhaustThenSucceedStore("53300");
        var channel = Channel.CreateUnbounded<ActivityLog>();
        for (var index = 0; index < 50; index++)
            channel.Writer.TryWrite(Entry($"first-{index}"));
        var writer = new ActivityLogWriter(
            channel, store, NullLogger<ActivityLogWriter>.Instance);

        await writer.StartAsync(CancellationToken.None);
        await store.Exhausted.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.TryWrite(Entry("after-recovery"));
        channel.Writer.TryComplete();
        await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        store.Calls.Should().Be(4);
        store.Saved.Should().ContainSingle(entry => entry.DeviceId == "after-recovery");
        metrics.Measurements.Should().ContainSingle(item =>
            item.Value == 50 && item.Outcome == "retry_exhausted");
    }

    [Fact]
    public async Task UndefinedTable_IsHostFatal_NoRetry_AndMetricIsExact()
    {
        using var metrics = new FailureMetricRecorder();
        var store = new FatalStore();
        var writer = Writer(store, [Entry("one")]);

        await writer.StartAsync(CancellationToken.None);
        var act = async () => await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.UndefinedTable);
        store.Calls.Should().Be(1);
        metrics.Measurements.Should().ContainSingle(item =>
            item.Value == 1 && item.Outcome == "fatal_contract");
    }

    [Fact]
    public async Task ChannelLifetime_ClosesIngressBeforeWriterStop_AndDrainsAcceptedRows()
    {
        var channel = Channel.CreateUnbounded<ActivityLog>();
        var store = new CollectingStore();
        var writer = new ActivityLogWriter(
            channel, store, NullLogger<ActivityLogWriter>.Instance);
        var lifetime = new ActivityLogChannelLifetime(channel);

        await writer.StartAsync(CancellationToken.None);
        channel.Writer.TryWrite(Entry("accepted-before-stop")).Should().BeTrue();
        await lifetime.StopAsync(CancellationToken.None);
        channel.Writer.TryWrite(Entry("rejected-after-stop")).Should().BeFalse();
        await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        store.Saved.Should().ContainSingle(entry => entry.DeviceId == "accepted-before-stop");
    }

    [Fact]
    public async Task ShutdownCancellation_RetriesBufferedBatchWithoutReportingLoss()
    {
        using var metrics = new FailureMetricRecorder();
        var channel = Channel.CreateUnbounded<ActivityLog>();
        var store = new CancelFirstStore();
        var writer = new ActivityLogWriter(
            channel, store, NullLogger<ActivityLogWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        channel.Writer.TryWrite(Entry("drained-after-cancel")).Should().BeTrue();
        await store.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await writer.StopAsync(deadline.Token);

        store.Saved.Should().ContainSingle(entry => entry.DeviceId == "drained-after-cancel");
        metrics.Measurements.Should().BeEmpty();
    }

    private static ActivityLogWriter Writer(
        IActivityLogBatchStore store, IReadOnlyList<ActivityLog> entries)
    {
        var channel = Channel.CreateUnbounded<ActivityLog>();
        foreach (var entry in entries) channel.Writer.TryWrite(entry);
        channel.Writer.TryComplete();
        return new ActivityLogWriter(
            channel, store, NullLogger<ActivityLogWriter>.Instance);
    }

    private static async Task RunToCompletionAsync(ActivityLogWriter writer)
    {
        await writer.StartAsync(CancellationToken.None);
        await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ActivityLog Entry(string deviceId) => new()
    {
        DeviceId = deviceId,
        Action = "assets_list",
        StatusCode = 200,
    };

    private static PostgresException Postgres(string sqlState) => new(
        "test", "ERROR", "ERROR", sqlState);

    private sealed class ToxicStore : IActivityLogBatchStore
    {
        public int Calls { get; private set; }
        public List<ActivityLog> Saved { get; } = [];

        public Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
        {
            Calls++;
            if (entries.Any(entry => entry.DeviceId == "toxic"))
                throw new DbUpdateException("constraint", Postgres("23514"));
            Saved.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    private sealed class RetryTwiceStore(string sqlState) : IActivityLogBatchStore
    {
        public int Calls { get; private set; }
        public List<int> BatchSizes { get; } = [];
        public List<ActivityLog> Saved { get; } = [];

        public Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
        {
            Calls++;
            BatchSizes.Add(entries.Count);
            if (Calls < 3)
                throw new DbUpdateException("transient", Postgres(sqlState));
            Saved.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    private sealed class ExhaustThenSucceedStore(string sqlState) : IActivityLogBatchStore
    {
        private readonly TaskCompletionSource _exhausted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }
        public Task Exhausted => _exhausted.Task;
        public List<ActivityLog> Saved { get; } = [];

        public Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
        {
            Calls++;
            if (Calls <= 3)
            {
                if (Calls == 3) _exhausted.TrySetResult();
                throw new DbUpdateException("transient", Postgres(sqlState));
            }

            Saved.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    private sealed class CollectingStore : IActivityLogBatchStore
    {
        public List<ActivityLog> Saved { get; } = [];

        public Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
        {
            Saved.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    private sealed class CancelFirstStore : IActivityLogBatchStore
    {
        private readonly TaskCompletionSource firstCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public Task FirstCallStarted => firstCallStarted.Task;
        public List<ActivityLog> Saved { get; } = [];

        public async Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstCallStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            Saved.AddRange(entries);
        }
    }

    private sealed class FatalStore : IActivityLogBatchStore
    {
        public int Calls { get; private set; }
        public Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
        {
            Calls++;
            throw Postgres(PostgresErrorCodes.UndefinedTable);
        }
    }

    private sealed class FailureMetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<(long Value, string? Outcome)> Measurements { get; } = [];

        public FailureMetricRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SaydinMetrics.MeterName
                    && instrument.Name == "saydin.activity_log.write.failures.total")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? outcome = null;
                foreach (var tag in tags)
                    if (tag.Key == "outcome") outcome = tag.Value?.ToString();
                Measurements.Add((value, outcome));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
