using System.Diagnostics.Metrics;
using FluentAssertions;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.IntegrationTests;

[Collection(IngestionDatabaseCollection.Name)]
public sealed class IngestionFreshnessHydrationIntegrationTests(IngestionDatabaseFixture database)
{
    [Fact]
    public async Task RestartHydration_ComesFromDurableTerminalWindowsAndActiveCalendarPointers()
    {
        var suspended = await database.SuspendActiveAssetsAsync(ProviderSources.CoinGecko);
        var assetId = Guid.Empty;
        var noJobAssetId = Guid.Empty;
        var date = new DateOnly(2041, 2, 3);
        try
        {
            assetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
            var scope = new IngestionWindowScope(
                ProviderSources.CoinGecko, assetId, IngestionJobTypes.DailyUpdate, 1);
            var repository = database.Repository();
            await repository.EnsureWindowsAsync(scope, [new(date, date)], default);
            var failed = (await repository.ClaimNextAsync(
                scope, "freshness-failure", TimeSpan.FromMinutes(1), default)).Claim!;
            await repository.RecordFailureAsync(
                failed, IngestionWindowStates.RetryableFailed,
                AdapterOutcomeKind.RetryableFailure,
                new IngestionWindowCounts(1, 1, 0, 0, 0, 0),
                "network_retryable", "network_retryable", null,
                DateTimeOffset.UtcNow.AddSeconds(-1), default);

            var failedState = await database.Repository().ReadFreshnessStateAsync(default);
            var failedStream = failedState.Streams.Single(stream =>
                stream.Source == ProviderSources.CoinGecko
                && stream.Cadence == IngestionCadence.Daily);
            failedStream.LastAttemptAt.Should().NotBeNull();
            failedStream.LastSuccessAt.Should().BeNull();
            failedStream.FailureStreak.Should().Be(1);

            var retry = (await repository.ClaimNextAsync(
                scope, "freshness-success", TimeSpan.FromMinutes(1), default)).Claim!;
            var point = AuthorityTestData.CoinGecko(
                assetId, $"it-{assetId:N}", date);
            await repository.CompletePriceAsync(
                retry, AdapterOutcome<PricePoint>.Data([point], 1),
                new IngestionWindowCounts(1, 1, 1, 1, 0, 0), default);

            var restartedState = await database.Repository().ReadFreshnessStateAsync(default);
            var restartedStream = restartedState.Streams.Single(stream =>
                stream.Source == ProviderSources.CoinGecko
                && stream.Cadence == IngestionCadence.Daily);
            restartedStream.LastSuccessAt.Should().NotBeNull();
            restartedStream.DataThrough.Should().Be(date);
            restartedStream.FailureStreak.Should().Be(0);

            noJobAssetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
            var missingScopeState = await database.Repository().ReadFreshnessStateAsync(default);
            var missingScopeStream = missingScopeState.Streams.Single(stream =>
                stream.Source == ProviderSources.CoinGecko
                && stream.Cadence == IngestionCadence.Daily);
            missingScopeStream.LastAttemptAt.Should().NotBeNull(
                "the successful sibling still owns the provider's latest attempt");
            missingScopeStream.LastSuccessAt.Should().BeNull(
                "an active asset with no job must fail closed behind no sibling");
            missingScopeStream.DataThrough.Should().BeNull();
            missingScopeStream.FailureStreak.Should().Be(0);

            var measurements = new List<MetricMeasurement>();
            using var listener = Listener(measurements);
            new IngestionFreshnessTelemetry().PublishState(missingScopeState,
                [new ExpectedFreshnessStream(ProviderSources.CoinGecko, IngestionCadence.Daily)]);
            measurements.Should().Contain(item =>
                item.Name == "saydin.ingestion.last_success.timestamp.seconds"
                && item.Value == 0
                && item.Source == ProviderSources.CoinGecko
                && item.Cadence == "daily");
            measurements.Should().Contain(item =>
                item.Name == "saydin.ingestion.lag.seconds"
                && item.Value == missingScopeState.DatabaseNow.ToUnixTimeSeconds()
                && item.Source == ProviderSources.CoinGecko
                && item.Cadence == "daily");
            restartedState.Calendars.Select(calendar => calendar.CalendarCode)
                .Should().Contain(["tcmb_indicative_fx", "bist_pay_xist"]);
        }
        finally
        {
            try
            {
                if (noJobAssetId != Guid.Empty) await database.CleanupAssetAsync(noJobAssetId);
                if (assetId != Guid.Empty) await database.CleanupAssetAsync(assetId);
            }
            finally
            {
                await database.RestoreActiveAssetsAsync(suspended);
            }
        }
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
        {
            var values = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString());
            measurements.Add(new MetricMeasurement(
                instrument.Name, measurement,
                values.GetValueOrDefault("source"), values.GetValueOrDefault("cadence")));
        });
        listener.Start();
        return listener;
    }

    private sealed record MetricMeasurement(
        string Name, long Value, string? Source, string? Cadence);
}
