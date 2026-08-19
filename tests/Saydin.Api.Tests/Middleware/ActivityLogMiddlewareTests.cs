using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Saydin.Api.Middleware;
using Saydin.Api.Services;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Middleware;

public sealed class ActivityLogMiddlewareTests
{
    [Theory]
    [InlineData(400, "request_invalid")]
    [InlineData(401, "authentication_failed")]
    [InlineData(403, "request_forbidden")]
    [InlineData(429, "rate_limited")]
    [InlineData(503, "service_unavailable")]
    [InlineData(500, "internal_error")]
    public async Task ProductFailuresBeforeHandler_AreAuditedWithStableOutcome(
        int status, string errorCode)
    {
        var logger = Substitute.For<IActivityLogger>();
        var sut = new ActivityLogMiddleware(
            logger, NullLogger<ActivityLogMiddleware>.Instance);
        var context = ProductContext("CalculateWhatIf");

        await sut.InvokeAsync(context, request =>
        {
            request.Response.StatusCode = status;
            return Task.CompletedTask;
        });

        logger.Received(1).Log(Arg.Is<ActivityLog>(entry =>
            entry.Action == "what_if_calculate"
            && entry.StatusCode == status
            && entry.ErrorCode == errorCode
            && entry.IpAddress == null
            && entry.Data == null));
    }

    [Theory]
    [InlineData(ApiPortRequestKind.PublicLiveness)]
    [InlineData(ApiPortRequestKind.Management)]
    public async Task HealthAndMetrics_AreExcluded(ApiPortRequestKind kind)
    {
        var logger = Substitute.For<IActivityLogger>();
        var sut = new ActivityLogMiddleware(
            logger, NullLogger<ActivityLogMiddleware>.Instance);
        var context = ProductContext("GetAssets");
        context.Items[ApiPortBoundary.RequestKindItemKey] = kind;

        await sut.InvokeAsync(context, _ => Task.CompletedTask);

        logger.DidNotReceiveWithAnyArgs().Log(default!);
    }

    private static DefaultHttpContext ProductContext(string endpointName)
    {
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(
                new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero)))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Items[ApiPortBoundary.RequestKindItemKey] = ApiPortRequestKind.PublicProduct;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new TestEndpointNameMetadata(endpointName)),
            endpointName));
        return context;
    }

    private sealed record TestEndpointNameMetadata(string EndpointName) : IEndpointNameMetadata;
}
