using System.Diagnostics.Metrics;
using FluentAssertions;
using Saydin.Api.Helpers;
using Saydin.Shared.Diagnostics;

namespace Saydin.Api.Tests.Helpers;

public sealed class CalculationTelemetryTests
{
    [Fact]
    public async Task ObserveWhatIfAsync_EmitsBoundedTerminalOutcomeAndDuration()
    {
        var counters = new List<(long Value, string Operation, string Outcome)>();
        var durations = new List<(double Value, string Operation, string Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == SaydinMetrics.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "saydin.whatif.calculations.total")
                counters.Add((value, Tag(tags, "operation"), Tag(tags, "outcome")));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "saydin.whatif.calculation.duration.ms")
                durations.Add((value, Tag(tags, "operation"), Tag(tags, "outcome")));
        });
        listener.Start();

        (await CalculationTelemetry.ObserveWhatIfAsync("calculate", () => Task.FromResult(7)))
            .Should().Be(7);
        await FluentActions.Awaiting(() => CalculationTelemetry.ObserveWhatIfAsync<int>(
                "compare", () => Task.FromException<int>(new InvalidOperationException("sentinel"))))
            .Should().ThrowAsync<InvalidOperationException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await FluentActions.Awaiting(() => CalculationTelemetry.ObserveWhatIfAsync<int>(
                "reverse", () => Task.FromCanceled<int>(cancellation.Token)))
            .Should().ThrowAsync<OperationCanceledException>();

        counters.Should().BeEquivalentTo(new[]
        {
            (1L, "calculate", "success"),
            (1L, "compare", "error"),
            (1L, "reverse", "cancelled"),
        });
        durations.Should().HaveCount(3);
        durations.Should().OnlyContain(measurement =>
            measurement.Value >= 0
            && new[] { "calculate", "compare", "reverse" }.Contains(measurement.Operation)
            && new[] { "success", "error", "cancelled" }.Contains(measurement.Outcome));
    }

    private static string Tag(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string key)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == key)
                return tag.Value?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }
}
