using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Saydin.Api.Middleware;
using Saydin.Api.Runtime;

namespace Saydin.Api.Tests.Middleware;

public sealed class ApiManagementBoundaryHttpTests
{
    [Fact]
    public async Task KestrelListeners_FailClosedAcrossPublicAndManagementPorts()
    {
        using var publicListener = CreateBoundListener();
        using var managementListener = CreateBoundListener();
        var publicPort = ((IPEndPoint)publicListener.LocalEndPoint!).Port;
        var managementPort = ((IPEndPoint)managementListener.LocalEndPoint!).Port;
        var runtime = new ApiRuntimeContract
        {
            PublicPort = publicPort,
            ManagementPort = managementPort,
            AllowedHosts = ["127.0.0.1"],
            KnownProxies = [IPAddress.Loopback],
            KnownNetworks = [],
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenHandle(unchecked((ulong)publicListener.Handle.ToInt64()));
            options.ListenHandle(unchecked((ulong)managementListener.Handle.ToInt64()));
        });
        builder.Services.AddSingleton(runtime);
        builder.Services.AddLocalization();
        builder.Services.AddTransient<ApiPortBoundaryMiddleware>();
        builder.Services.AddSingleton<Microsoft.AspNetCore.Routing.MatcherPolicy,
            ApiPortEndpointSelectorPolicy>();
        builder.Services.Configure<ForwardedHeadersOptions>(runtime.Configure);
        await using var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseMiddleware<ApiPortBoundaryMiddleware>();
        app.MapGet(ApiPortBoundary.LivePath, () => Results.Ok("live"))
            .WithMetadata(new ApiEndpointSurfaceMetadata(ApiEndpointSurface.PublicLiveness));
        app.MapGet(ApiPortBoundary.ReadyPath, () => Results.Ok("ready"))
            .WithMetadata(new ApiEndpointSurfaceMetadata(ApiEndpointSurface.Management));
        app.MapGet(ApiPortBoundary.MetricsPath, () => Results.Text("metric 1"))
            .WithMetadata(new ApiEndpointSurfaceMetadata(ApiEndpointSurface.Management));
        app.MapGet("/v1/product", () => Results.Ok("product"))
            .WithMetadata(new ApiEndpointSurfaceMetadata(ApiEndpointSurface.PublicProduct));
        await app.StartAsync();

        using var publicClient = Client(publicPort);
        using var managementClient = Client(managementPort);

        (await publicClient.GetAsync(ApiPortBoundary.LivePath)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await publicClient.GetAsync(ApiPortBoundary.ReadyPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await publicClient.GetAsync(ApiPortBoundary.MetricsPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var path in new[]
                 {
                     "/HEALTH/READY/",
                     "//health//ready//",
                     "/METRICS/",
                     "//metrics//",
                 })
        {
            using var response = await publicClient.GetAsync(
                new Uri($"http://127.0.0.1:{publicPort}{path}"));
            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                $"management path variant '{path}' must not match on the public listener");
        }
        (await publicClient.GetAsync("/v1/product")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await managementClient.GetAsync(ApiPortBoundary.LivePath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await managementClient.GetAsync(ApiPortBoundary.ReadyPath)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await managementClient.GetAsync(ApiPortBoundary.MetricsPath)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await managementClient.GetAsync("/v1/product")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var managementSpoof = new HttpRequestMessage(HttpMethod.Get, "/v1/product");
        managementSpoof.Headers.Host = $"127.0.0.1:{publicPort}";
        managementSpoof.Headers.TryAddWithoutValidation("X-Forwarded-Host", $"127.0.0.1:{publicPort}");
        managementSpoof.Headers.TryAddWithoutValidation("X-Forwarded-Port", publicPort.ToString());
        (await managementClient.SendAsync(managementSpoof)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var publicSpoof = new HttpRequestMessage(HttpMethod.Get, ApiPortBoundary.MetricsPath);
        publicSpoof.Headers.Host = $"127.0.0.1:{managementPort}";
        publicSpoof.Headers.TryAddWithoutValidation("X-Forwarded-Host", $"127.0.0.1:{managementPort}");
        publicSpoof.Headers.TryAddWithoutValidation("X-Forwarded-Port", managementPort.ToString());
        (await publicClient.SendAsync(publicSpoof)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static HttpClient Client(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
    };

    private static Socket CreateBoundListener()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        return listener;
    }
}
