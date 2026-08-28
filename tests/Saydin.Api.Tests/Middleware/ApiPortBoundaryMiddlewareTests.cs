using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Saydin.Api;
using Saydin.Api.Middleware;
using Saydin.Api.Runtime;

namespace Saydin.Api.Tests.Middleware;

public sealed class ApiPortBoundaryMiddlewareTests
{
    public static TheoryData<int, string, ApiPortRequestKind> Matrix => new()
    {
        { 8080, "/health/live", ApiPortRequestKind.PublicLiveness },
        { 8080, "/health/ready", ApiPortRequestKind.Rejected },
        { 8080, "/HEALTH/READY/", ApiPortRequestKind.Rejected },
        { 8080, "//health//ready//", ApiPortRequestKind.Rejected },
        { 8080, "/metrics", ApiPortRequestKind.Rejected },
        { 8080, "//METRICS//", ApiPortRequestKind.Rejected },
        { 8080, "/api/v1/assets", ApiPortRequestKind.PublicProduct },
        { 9090, "/health/live", ApiPortRequestKind.Rejected },
        { 9090, "/health/ready", ApiPortRequestKind.Management },
        { 9090, "//HEALTH//READY//", ApiPortRequestKind.Management },
        { 9090, "/metrics", ApiPortRequestKind.Management },
        { 9090, "//METRICS//", ApiPortRequestKind.Management },
        { 9090, "/api/v1/assets", ApiPortRequestKind.Rejected },
        { 7070, "/api/v1/assets", ApiPortRequestKind.Rejected },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Classify_UsesActualListenerPort_NotHostOrForwardedHeaders(
        int localPort, string path, ApiPortRequestKind expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;
        context.Request.Path = path;
        context.Request.Host = new HostString("spoofed.example", 9090);
        context.Request.Headers["X-Forwarded-Host"] = "management.example:9090";
        context.Request.Headers["X-Forwarded-Port"] = "9090";

        ApiPortBoundaryMiddleware.Classify(context, Runtime(), Production())
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(ApiEndpointSurface.Management, 8080, false)]
    [InlineData(ApiEndpointSurface.Management, 9090, true)]
    [InlineData(ApiEndpointSurface.PublicProduct, 8080, true)]
    [InlineData(ApiEndpointSurface.PublicProduct, 9090, false)]
    [InlineData(ApiEndpointSurface.PublicLiveness, 8080, true)]
    [InlineData(ApiEndpointSurface.PublicLiveness, 9090, false)]
    public async Task EndpointSelector_RemovesCrossSurfaceCandidatesBeforeExecution(
        ApiEndpointSurface surface,
        int port,
        bool expected)
    {
        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new ApiEndpointSurfaceMetadata(surface)),
            $"surface:{surface}");
        var candidates = new CandidateSet(
            [endpoint],
            [new RouteValueDictionary()],
            [0]);
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = port;
        var policy = new ApiPortEndpointSelectorPolicy(Runtime(), Production());

        await policy.ApplyAsync(context, candidates);

        candidates.IsValidCandidate(0).Should().Be(expected);
    }

    [Theory]
    [InlineData(ApiPortRequestKind.PublicLiveness, true)]
    [InlineData(ApiPortRequestKind.Management, true)]
    [InlineData(ApiPortRequestKind.PublicProduct, false)]
    [InlineData(ApiPortRequestKind.Rejected, false)]
    public void AdmissionExemption_IsOnlyLivenessAndManagement(
        ApiPortRequestKind kind, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Items[ApiPortBoundary.RequestKindItemKey] = kind;
        ApiPortBoundary.IsAdmissionExempt(context).Should().Be(expected);
    }

    [Fact]
    public async Task Invoke_RejectedRoute_ReturnsEmpty404WithoutCallingProductPipeline()
    {
        var middleware = new ApiPortBoundaryMiddleware(
            Runtime(), Production(), CreateLocalizer());
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 9090;
        context.Request.Path = "/api/v1/assets";
        var called = false;

        await middleware.InvokeAsync(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        called.Should().BeFalse();
    }

    private static ApiRuntimeContract Runtime() => new()
    {
        PublicPort = 8080,
        ManagementPort = 9090,
        AllowedHosts = ["api.example.test"],
        KnownProxies = [],
        KnownNetworks = [],
    };

    private static IHostEnvironment Production()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        return environment;
    }

    private static IStringLocalizer<ErrorMessages> CreateLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        return services.BuildServiceProvider()
            .GetRequiredService<IStringLocalizer<ErrorMessages>>();
    }
}
