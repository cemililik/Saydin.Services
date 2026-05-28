using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Saydin.Api.Endpoints;
using Saydin.Api.Helpers;

namespace Saydin.Api.Tests.Helpers;

public class ActivityLogBuilderTests
{
    private static HttpContext CreateHttpContext(
        string? deviceId = "test-device",
        string? deviceOs = null,
        string? appVersion = null)
    {
        var context = new DefaultHttpContext();
        if (deviceId is not null)
            context.Items[EndpointExtensions.DeviceIdItemKey] = deviceId;
        if (deviceOs is not null)
            context.Request.Headers["X-Device-OS"] = deviceOs;
        if (appVersion is not null)
            context.Request.Headers["X-App-Version"] = appVersion;
        return context;
    }

    [Fact]
    public void Build_SetsActionAndStatusCode()
    {
        var ctx = CreateHttpContext();

        var log = new ActivityLogBuilder(ctx)
            .WithAction("what_if_calculate")
            .Build();

        log.Action.Should().Be("what_if_calculate");
        log.StatusCode.Should().Be(200);
        log.DeviceId.Should().Be("test-device");
    }

    [Fact]
    public void Build_WithData_SerializesToJsonElement()
    {
        var ctx = CreateHttpContext();

        var log = new ActivityLogBuilder(ctx)
            .WithAction("what_if_calculate")
            .WithData(new { assetSymbol = "USDTRY", amount = 10000 })
            .Build();

        log.Data.Should().NotBeNull();
        log.Data!.Value.GetProperty("assetSymbol").GetString().Should().Be("USDTRY");
        log.Data!.Value.GetProperty("amount").GetInt32().Should().Be(10000);
    }

    [Fact]
    public void Build_WithError_SetsStatusCodeAndErrorCode()
    {
        var ctx = CreateHttpContext();

        var log = new ActivityLogBuilder(ctx)
            .WithAction("what_if_calculate")
            .WithError(429, "daily-limit-exceeded")
            .Build();

        log.StatusCode.Should().Be(429);
        log.ErrorCode.Should().Be("daily-limit-exceeded");
    }

    [Fact]
    public void Build_ReadsDeviceHeaders()
    {
        var ctx = CreateHttpContext(deviceOs: "android", appVersion: "0.1.1+43");

        var log = new ActivityLogBuilder(ctx)
            .WithAction("config_fetch")
            .Build();

        log.DeviceOs.Should().Be("android");
        log.AppVersion.Should().Be("0.1.1+43");
    }

    [Fact]
    public void Build_MeasuresDuration()
    {
        var ctx = CreateHttpContext();

        var log = new ActivityLogBuilder(ctx)
            .WithAction("what_if_calculate")
            .Build();

        log.DurationMs.Should().NotBeNull();
        log.DurationMs!.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Build_NoDeviceId_FallsBackToUnknown()
    {
        var ctx = CreateHttpContext(deviceId: null);

        var log = new ActivityLogBuilder(ctx)
            .WithAction("assets_list")
            .Build();

        log.DeviceId.Should().Be("unknown");
    }

    [Fact]
    public void Build_WithUserId_SetsUserId()
    {
        var ctx = CreateHttpContext();
        var userId = Guid.NewGuid();

        var log = new ActivityLogBuilder(ctx)
            .WithAction("what_if_calculate")
            .WithUserId(userId)
            .Build();

        log.UserId.Should().Be(userId);
    }

    [Fact]
    public void Build_MasksIpAddress()
    {
        var ctx = CreateHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.42");

        var log = new ActivityLogBuilder(ctx)
            .WithAction("what_if_calculate")
            .Build();

        log.IpAddress.Should().Be(System.Net.IPAddress.Parse("192.168.1.0"));
    }
}
