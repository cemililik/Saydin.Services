using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Saydin.Api.Services;
using Saydin.Api.Tests.Helpers;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Services;

[Collection(MetricsTestCollection.Name)]
public class ChannelActivityLoggerTests
{
    [Fact]
    public void Log_WritesEntryToChannel()
    {
        var channel = Channel.CreateUnbounded<ActivityLog>();
        var telemetry = CreateTelemetry(out _, out _);
        var sut = new ChannelActivityLogger(channel, telemetry);

        var entry = new ActivityLog
        {
            DeviceId = "test-device",
            Action = "what_if_calculate",
            StatusCode = 200,
        };

        sut.Log(entry);

        channel.Reader.TryRead(out var result).Should().BeTrue();
        result.Should().BeSameAs(entry);
    }

    [Fact]
    public void Log_CapacityOneDropWrite_RecordsExactlyOneDropWithAllowlistedTag()
    {
        using var metrics = new MetricRecorder(
            "saydin.activity_log.queue.drops.total",
            "saydin.activity_log.queue.rejected_writes.total");
        var telemetry = CreateTelemetry(out var logger, out _);
        var channel = Channel.CreateBounded<ActivityLog>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite },
            telemetry.RecordDropped);
        var sut = new ChannelActivityLogger(channel, telemetry);

        var retained = new ActivityLog
        {
            DeviceId = "d1", Action = "what_if_calculate", StatusCode = 200,
        };
        const string arbitraryAction = "attacker-controlled-action";
        var dropped = new ActivityLog
        {
            DeviceId = "d2", Action = arbitraryAction, StatusCode = 200,
        };

        sut.Log(retained);
        sut.Log(dropped);

        channel.Reader.TryRead(out var result).Should().BeTrue();
        result.Should().BeSameAs(retained);
        channel.Reader.TryRead(out _).Should().BeFalse();

        var measurement = metrics.Measurements.Should().ContainSingle(item =>
            item.InstrumentName == "saydin.activity_log.queue.drops.total").Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags.Should().Contain("action", "unknown");
        metrics.Measurements.Should().NotContain(item =>
            item.InstrumentName == "saydin.activity_log.queue.rejected_writes.total");

        var warning = logger.Entries.Should().ContainSingle(item => item.Level == LogLevel.Warning).Subject;
        warning.Properties.Should().Contain("Action", "unknown");
        warning.Message.Should().NotContain(arbitraryAction);
    }

    [Fact]
    public void Log_CompletedWriter_RecordsRejectedWriteButNotDrop()
    {
        using var metrics = new MetricRecorder(
            "saydin.activity_log.queue.drops.total",
            "saydin.activity_log.queue.rejected_writes.total");
        var telemetry = CreateTelemetry(out var logger, out _);
        var channel = Channel.CreateBounded<ActivityLog>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite },
            telemetry.RecordDropped);
        var sut = new ChannelActivityLogger(channel, telemetry);
        channel.Writer.TryComplete().Should().BeTrue();

        sut.Log(new ActivityLog
        {
            DeviceId = "completed-device",
            Action = "what_if_calculate",
            StatusCode = 200,
        });

        metrics.Measurements.Should().NotContain(item =>
            item.InstrumentName == "saydin.activity_log.queue.drops.total");
        var measurement = metrics.Measurements.Should().ContainSingle(item =>
            item.InstrumentName == "saydin.activity_log.queue.rejected_writes.total").Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags.Should().Contain("action", "what_if_calculate");
        measurement.Tags.Should().Contain("reason", "writer_completed");
        logger.Entries.Should().ContainSingle(item => item.Level == LogLevel.Warning);
    }

    [Fact]
    public void Telemetry_RepeatedEvents_RateLimitsWarningsSeparatelyByOutcome()
    {
        var telemetry = CreateTelemetry(out var logger, out var timeProvider);
        var entry = new ActivityLog
        {
            DeviceId = "test-device", Action = "what_if_calculate", StatusCode = 200,
        };

        telemetry.RecordDropped(entry);
        telemetry.RecordDropped(entry);
        telemetry.RecordRejected(entry);
        telemetry.RecordRejected(entry);

        logger.Entries.Should().HaveCount(2, "drop ve rejected warning'leri ayrı pencereye sahiptir");

        timeProvider.Advance(ActivityLogChannelTelemetry.WarningInterval);
        telemetry.RecordDropped(entry);
        telemetry.RecordRejected(entry);

        logger.Entries.Should().HaveCount(4);
    }

    [Fact]
    public void Log_MultipleEntries_AllWrittenInOrder()
    {
        var channel = Channel.CreateUnbounded<ActivityLog>();
        var telemetry = CreateTelemetry(out _, out _);
        var sut = new ChannelActivityLogger(channel, telemetry);

        for (var i = 0; i < 5; i++)
        {
            sut.Log(new ActivityLog
            {
                DeviceId = $"device-{i}",
                Action = "what_if_calculate",
                StatusCode = 200,
            });
        }

        var readBack = new List<ActivityLog>();
        while (channel.Reader.TryRead(out var entry))
            readBack.Add(entry);

        readBack.Should().HaveCount(5);
        readBack.Select(e => e.DeviceId).Should().Equal("device-0", "device-1", "device-2", "device-3", "device-4");
    }

    private static ActivityLogChannelTelemetry CreateTelemetry(
        out TestLogger<ActivityLogChannelTelemetry> logger,
        out FakeTimeProvider timeProvider)
    {
        logger = new TestLogger<ActivityLogChannelTelemetry>();
        timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        return new ActivityLogChannelTelemetry(timeProvider, logger);
    }

    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly HashSet<string> _instrumentNames;
        private readonly ConcurrentQueue<MetricMeasurement> _measurements = new();

        internal MetricRecorder(params string[] instrumentNames)
        {
            _instrumentNames = instrumentNames.ToHashSet(StringComparer.Ordinal);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SaydinMetrics.MeterName
                    && _instrumentNames.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var copiedTags = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                    copiedTags[tag.Key] = tag.Value;

                _measurements.Enqueue(new MetricMeasurement(instrument.Name, value, copiedTags));
            });
            _listener.Start();
        }

        internal IReadOnlyCollection<MetricMeasurement> Measurements => _measurements.ToArray();

        public void Dispose() => _listener.Dispose();
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        long Value,
        IReadOnlyDictionary<string, object?> Tags);
}
