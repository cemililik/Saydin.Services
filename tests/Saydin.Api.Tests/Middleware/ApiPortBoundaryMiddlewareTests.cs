using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Saydin.Api.Middleware;
using Saydin.Api.Runtime;

namespace Saydin.Api.Tests.Middleware;

public sealed class ApiPortBoundaryMiddlewareTests
{
    public static TheoryData<int, string, ApiPortRequestKind> Matrix => new()
    {
        { 8080, "/health/live", ApiPortRequestKind.PublicLiveness },
        { 8080, "/health/ready", ApiPortRequestKind.Rejected },
        { 8080, "/metrics", ApiPortRequestKind.Rejected },
        { 8080, "/api/v1/assets", ApiPortRequestKind.PublicProduct },
        { 9090, "/health/live", ApiPortRequestKind.Rejected },
        { 9090, "/health/ready", ApiPortRequestKind.Management },
        { 9090, "/metrics", ApiPortRequestKind.Management },
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
        var middleware = new ApiPortBoundaryMiddleware(Runtime(), Production());
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
}
