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

public sealed class ActivityLogWriterTests
{
    [Theory]
    [InlineData("23514", "ToxicRow")]
    [InlineData("23503", "ToxicRow")]
    [InlineData("40001", "TransientBatch")]
    [InlineData("40P01", "TransientBatch")]
    [InlineData("08006", "TransientBatch")]
    [InlineData("42P01", "FatalHost")]
    [InlineData("42501", "FatalHost")]
    [InlineData("22003", "FatalHost")]
    public void Classifier_UsesExactSqlStateClasses(
        string sqlState, string expected)
    {
        var exception = new DbUpdateException("write failed", Postgres(sqlState));
        ActivityLogWriteFailureClassifier.Classify(exception).ToString().Should().Be(expected);
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
        var store = new RetryTwiceStore();
        var entries = new[] { Entry("one"), Entry("two") };
        var writer = Writer(store, entries);

        await RunToCompletionAsync(writer);

        store.Calls.Should().Be(3);
        store.BatchSizes.Should().Equal(2, 2, 2);
        store.Saved.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task UndefinedTable_IsHostFatal_NoRetryOrDrop()
    {
        using var metrics = new FailureMetricRecorder();
        var store = new FatalStore();
        var writer = Writer(store, [Entry("one")]);

        await writer.StartAsync(CancellationToken.None);
        var act = async () => await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.UndefinedTable);
        store.Calls.Should().Be(1);
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

    private sealed class RetryTwiceStore : IActivityLogBatchStore
    {
        public int Calls { get; private set; }
        public List<int> BatchSizes { get; } = [];
        public List<ActivityLog> Saved { get; } = [];

        public Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
        {
            Calls++;
            BatchSizes.Add(entries.Count);
            if (Calls < 3)
                throw new DbUpdateException("serialization", Postgres("40001"));
            Saved.AddRange(entries);
            return Task.CompletedTask;
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
