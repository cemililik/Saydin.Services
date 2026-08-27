using System.Diagnostics.Metrics;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Diagnostics;

namespace Saydin.PriceIngestion.Tests.Repositories;

public sealed class IngestionFreshnessTelemetryTests
{
    [Fact]
    public void OnlyAuthoritativeTerminalSuccess_AdvancesLastSuccess()
    {
        var measurements = new List<MetricMeasurement>();
        using var listener = Listener(measurements);
        var telemetry = new IngestionFreshnessTelemetry();
        var started = new DateTimeOffset(2026, 8, 19, 8, 0, 0, TimeSpan.Zero);
        var failed = started.AddSeconds(5);
        var succeeded = started.AddMinutes(1);
        var counts = new IngestionWindowCounts(1, 1, 1, 1, 0, 0);

        telemetry.RecordStarted("tcmb", IngestionCadence.Daily, started);
        telemetry.RecordTerminal(
            "tcmb", IngestionCadence.Daily, AdapterOutcomeKind.RetryableFailure,
            counts, started, failed, new DateOnly(2026, 8, 18), authoritativeSuccess: false);
        telemetry.RecordTerminal(
            "tcmb", IngestionCadence.Daily, AdapterOutcomeKind.Data,
            counts, started, succeeded, new DateOnly(2026, 8, 18), authoritativeSuccess: true);

        Assert.DoesNotContain(measurements, item =>
            item.Name == "saydin.ingestion.last_success.timestamp.seconds");
        telemetry.PublishState(new IngestionFreshnessState(succeeded,
        [
            new IngestionFreshnessSnapshot(
                "tcmb", IngestionCadence.Daily, started, succeeded,
                new DateOnly(2026, 8, 18), 0),
        ], []), [new ExpectedFreshnessStream("tcmb", IngestionCadence.Daily)]);

        var success = Assert.Single(measurements, item =>
            item.Name == "saydin.ingestion.last_success.timestamp.seconds");
        Assert.Equal(succeeded.ToUnixTimeSeconds(), success.LongValue);
        Assert.Equal("tcmb", success.Tags["source"]);
        Assert.Equal("daily", success.Tags["cadence"]);
        Assert.Contains(measurements, item =>
            item.Name == "saydin.ingestion.attempts.total"
            && item.Tags.GetValueOrDefault("outcome") == "retryable_failure");
        Assert.Contains(measurements, item =>
            item.Name == "saydin.ingestion.attempts.total"
            && item.Tags.GetValueOrDefault("outcome") == "success");
        Assert.DoesNotContain(measurements.SelectMany(item => item.Tags.Keys),
            key => key is "asset" or "symbol" or "window_id");
    }

    [Fact]
    public void Hydration_EmitsDurableDailyMonthlyAndCalendarMissingSeries()
    {
        var measurements = new List<MetricMeasurement>();
        using var listener = Listener(measurements);
        var telemetry = new IngestionFreshnessTelemetry();
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var success = now.AddDays(-10);
        var state = new IngestionFreshnessState(now,
        [
            new IngestionFreshnessSnapshot(
                "evds", IngestionCadence.Monthly, success, success,
                new DateOnly(2026, 7, 1), 2),
        ], []);

        telemetry.PublishState(state,
        [
            new ExpectedFreshnessStream("tcmb", IngestionCadence.Daily),
            new ExpectedFreshnessStream("evds", IngestionCadence.Monthly),
        ]);

        Assert.Contains(measurements, item =>
            item.Name == "saydin.ingestion.last_success.timestamp.seconds"
            && item.LongValue == 0
            && item.Tags.GetValueOrDefault("source") == "tcmb"
            && item.Tags.GetValueOrDefault("cadence") == "daily");
        Assert.Contains(measurements, item =>
            item.Name == "saydin.ingestion.last_success.timestamp.seconds"
            && item.LongValue == success.ToUnixTimeSeconds()
            && item.Tags.GetValueOrDefault("source") == "evds"
            && item.Tags.GetValueOrDefault("cadence") == "monthly");
        Assert.Contains(measurements, item =>
            item.Name == "saydin.ingestion.failure_streak"
            && item.LongValue == 2
            && item.Tags.GetValueOrDefault("source") == "evds");
        Assert.Equal(2, measurements.Count(item =>
            item.Name == "saydin.market_calendar.coverage.horizon.days"
            && item.LongValue == -1_000_000));
    }

    private static MeterListener Listener(ICollection<MetricMeasurement> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == SaydinMetrics.MeterName)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            measurements.Add(new MetricMeasurement(
                instrument.Name, measurement, null, TagDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            measurements.Add(new MetricMeasurement(
                instrument.Name, null, measurement, TagDictionary(tags))));
        listener.Start();
        return listener;
    }

    private static Dictionary<string, string> TagDictionary(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags) result[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        return result;
    }

    private sealed record MetricMeasurement(
        string Name,
        long? LongValue,
        double? DoubleValue,
        Dictionary<string, string> Tags);
}
