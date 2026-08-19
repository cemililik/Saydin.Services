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
        var publicPort = ReservePort();
        var managementPort = ReservePort(publicPort);
        var runtime = new ApiRuntimeContract
        {
            PublicPort = publicPort,
            ManagementPort = managementPort,
            AllowedHosts = ["127.0.0.1"],
            KnownProxies = [IPAddress.Loopback],
            KnownNetworks = [],
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(runtime.Configure);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddTransient<ApiPortBoundaryMiddleware>();
        builder.Services.Configure<ForwardedHeadersOptions>(runtime.Configure);
        await using var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseMiddleware<ApiPortBoundaryMiddleware>();
        app.MapGet(ApiPortBoundary.LivePath, () => Results.Ok("live"));
        app.MapGet(ApiPortBoundary.ReadyPath, () => Results.Ok("ready"));
        app.MapGet(ApiPortBoundary.MetricsPath, () => Results.Text("metric 1"));
        app.MapGet("/v1/product", () => Results.Ok("product"));
        await app.StartAsync();

        using var publicClient = Client(publicPort);
        using var managementClient = Client(managementPort);

        (await publicClient.GetAsync(ApiPortBoundary.LivePath)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await publicClient.GetAsync(ApiPortBoundary.ReadyPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await publicClient.GetAsync(ApiPortBoundary.MetricsPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    private static int ReservePort(int excluded = -1)
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != excluded)
                return port;
        }
    }
}
