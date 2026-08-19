using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

public sealed class IngestionFreshnessHydrationService(
    IIngestionWindowRepository windows,
    IIngestionFreshnessTelemetry telemetry,
    IConfiguration configuration,
    ILogger<IngestionFreshnessHydrationService> logger) : BackgroundService
{
    private static readonly (string Worker, string Source, IngestionCadence Cadence)[] KnownStreams =
    [
        ("Tcmb", "tcmb", IngestionCadence.Daily),
        ("CoinGecko", "coingecko", IngestionCadence.Daily),
        ("OpenExchangeRates", "openexchangerates", IngestionCadence.Daily),
        ("TwelveData", "twelvedata", IngestionCadence.Daily),
        ("EvdsInflation", "evds", IngestionCadence.Monthly),
    ];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Clamp(configuration.GetValue<int?>(
            "IngestionFreshness:RefreshSeconds") ?? 60, 15, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RefreshAsync(stoppingToken);
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        var expected = KnownStreams
            .Where(stream => configuration.GetValue<bool?>(
                $"IngestionWorkers:{stream.Worker}:Enabled") ?? false)
            .Select(stream => new ExpectedFreshnessStream(stream.Source, stream.Cadence))
            .ToArray();
        var state = await windows.ReadFreshnessStateAsync(ct);
        telemetry.PublishState(state, expected);
        logger.LogDebug(
            "Durable ingestion freshness hydrate edildi: {StreamCount} stream, {CalendarCount} calendar",
            state.Streams.Count, state.Calendars.Count);
    }
}
