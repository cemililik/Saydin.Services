using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.PriceIngestion.Repositories;
using Saydin.PriceIngestion.Workers;

namespace Saydin.PriceIngestion.Tests.Workers;

public sealed class IngestionFreshnessHydrationServiceTests
{
    [Fact]
    public async Task TransientDatabaseFailure_DoesNotEscapeHostedServiceBoundary()
    {
        var windows = Substitute.For<IIngestionWindowRepository>();
        windows.ReadFreshnessStateAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IngestionFreshnessState>>(_ => throw new TimeoutException("db timeout"));
        var service = Service(windows);

        var act = () => service.RefreshSafelyAsync(default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HostCancellation_IsNotMisclassifiedAsHydrationFailure()
    {
        var windows = Substitute.For<IIngestionWindowRepository>();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        windows.ReadFreshnessStateAsync(stop.Token)
            .Returns<Task<IngestionFreshnessState>>(_ => throw new OperationCanceledException(stop.Token));
        var service = Service(windows);

        var act = () => service.RefreshSafelyAsync(stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static IngestionFreshnessHydrationService Service(
        IIngestionWindowRepository windows) => new(
        windows,
        Substitute.For<IIngestionFreshnessTelemetry>(),
        new ConfigurationBuilder().Build(),
        NullLogger<IngestionFreshnessHydrationService>.Instance);
}
