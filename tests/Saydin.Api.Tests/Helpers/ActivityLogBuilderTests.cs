using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Saydin.Api.Endpoints;
using Saydin.Api.Helpers;

namespace Saydin.Api.Tests.Helpers;

public class ActivityLogBuilderTests
{
    private static HttpContext CreateHttpContext(
        string? principalActivityId = "p1:test-principal",
        string? deviceOs = null,
        string? appVersion = null)
    {
        var context = new DefaultHttpContext();
        if (principalActivityId is not null)
            context.Items[EndpointExtensions.PrincipalActivityIdItemKey] = principalActivityId;
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
        log.DeviceId.Should().Be("p1:test-principal");
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
    public void Build_UsesInjectedTimeProviderForCreatedAtAndDuration()
    {
        var now = new DateTimeOffset(2026, 8, 19, 10, 30, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var builder = new ActivityLogBuilder(CreateHttpContext(), timeProvider: time)
            .WithAction("assets_list");
        time.Advance(TimeSpan.FromMilliseconds(125));

        var log = builder.Build();

        log.CreatedAt.Should().Be(now.AddMilliseconds(125));
        log.DurationMs.Should().Be(125);
    }

    [Theory]
    [InlineData(400, "request_invalid")]
    [InlineData(401, "authentication_failed")]
    [InlineData(403, "request_forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(413, "payload_too_large")]
    [InlineData(415, "unsupported_media_type")]
    [InlineData(422, "unprocessable_entity")]
    [InlineData(429, "rate_limited")]
    [InlineData(502, "bad_gateway")]
    [InlineData(503, "service_unavailable")]
    [InlineData(500, "internal_error")]
    public void Build_ResponseFailuresReceiveStableBoundedErrorCode(
        short status, string errorCode)
    {
        var log = new ActivityLogBuilder(CreateHttpContext())
            .WithAction("assets_list")
            .WithResponseStatus(status)
            .Build();

        log.StatusCode.Should().Be(status);
        log.ErrorCode.Should().Be(errorCode);
        log.ErrorCode!.Length.Should().BeLessThanOrEqualTo(50);
    }

    [Fact]
    public void Build_NoDeviceId_FallsBackToUnknown()
    {
        var ctx = CreateHttpContext(principalActivityId: null);

        var log = new ActivityLogBuilder(ctx)
            .WithAction("assets_list")
            .Build();

        log.DeviceId.Should().Be("unknown");
    }

    [Fact]
    public void Build_WideNumericObject_UsesJsonbBinaryUpperBoundAndTruncates()
    {
        var payload = Enumerable.Range(0, 700)
            .ToDictionary(index => $"k{index}", _ => 0);
        var jsonTextBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload));
        jsonTextBytes.Should().BeLessThan(10_000,
            "the old JSON-text estimator must consider this payload safe");

        var log = new ActivityLogBuilder(CreateHttpContext())
            .WithAction("assets_list")
            .WithData(payload)
            .Build();

        log.Data.Should().NotBeNull();
        log.Data!.Value.GetProperty("_truncated").GetBoolean().Should().BeTrue();
        log.Data.Value.GetProperty("estimatedJsonbBytes").GetInt64()
            .Should().BeGreaterThan(10_000);
    }

    [Fact]
    public void JsonbUpperBound_PreservesSmallPayloadAndCoversRootScalar()
    {
        using var document = JsonDocument.Parse("""{"symbol":"USDTRY","amount":10000}""");
        var estimate = JsonbStorageSize.UpperBound(document.RootElement);
        var scalarEstimate = JsonbStorageSize.UpperBound(
            JsonSerializer.SerializeToElement(42));

        estimate.Should().BeGreaterThan(0).And.BeLessThan(10_000);
        scalarEstimate.Should().BeGreaterThan(0);
        new ActivityLogBuilder(CreateHttpContext())
            .WithAction("assets_list")
            .WithData(document.RootElement)
            .Build().Data!.Value.GetProperty("symbol").GetString().Should().Be("USDTRY");
    }

    [Theory]
    [InlineData("1e100000")]
    [InlineData("1e+100000")]
    [InlineData("9.99e100000")]
    [InlineData("-9.99e+100000")]
    [InlineData("1e-100000")]
    [InlineData("9.99e-100000")]
    public void JsonbUpperBound_LargeExponent_IsSaturatingAndTruncated(string number)
    {
        using var document = JsonDocument.Parse(number);

        var estimate = JsonbStorageSize.UpperBound(document.RootElement);
        var log = new ActivityLogBuilder(CreateHttpContext())
            .WithAction("assets_list")
            .WithData(document.RootElement)
            .Build();

        estimate.Should().BeGreaterThan(10_000);
        log.Data!.Value.GetProperty("_truncated").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("1e999999999999999999999999999999999999999")]
    [InlineData("1e-999999999999999999999999999999999999999")]
    public void JsonbUpperBound_OverflowingExponent_Saturates(string number)
    {
        using var document = JsonDocument.Parse(number);

        JsonbStorageSize.UpperBound(document.RootElement).Should().Be(long.MaxValue);
    }

    [Theory]
    [InlineData("0", 31)]
    [InlineData("0e999999999999999999999", 31)]
    [InlineData("123.45", 40)]
    [InlineData("-123.45", 40)]
    [InlineData("12345.6", 40)]
    [InlineData("1.23e2", 40)]
    [InlineData("1.23e-2", 40)]
    public void JsonbUpperBound_NormalNumber_RemainsSmall(string number, long maximum)
    {
        using var document = JsonDocument.Parse(number);

        JsonbStorageSize.UpperBound(document.RootElement).Should().BeLessThan(maximum);
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

    [Fact]
    public void Build_TruncatesOversizedHeaders()
    {
        // F2.1-12: DB kolon kapasitelerinin üstündeki header değerleri sessizce kırpılır.
        // X-Device-OS: 30 karakter, X-App-Version: 50 karakter.
        var longOs       = new string('A', 60);
        var longVersion  = new string('B', 70);

        var ctx = CreateHttpContext(deviceOs: longOs, appVersion: longVersion);

        var log = new ActivityLogBuilder(ctx)
            .WithAction("config_fetch")
            .Build();

        log.DeviceOs.Should().Be(new string('A', 30));
        log.AppVersion.Should().Be(new string('B', 50));
    }

    [Fact]
    public void Build_PreservesNormalSizedHeaders()
    {
        var ctx = CreateHttpContext(deviceOs: "android", appVersion: "0.1.1+43");

        var log = new ActivityLogBuilder(ctx)
            .WithAction("config_fetch")
            .Build();

        log.DeviceOs.Should().Be("android");
        log.AppVersion.Should().Be("0.1.1+43");
    }

    [Fact]
    public void Build_NoActionConfigured_ThrowsInvalidOperationException()
    {
        // F2.1-8: Build() çağrılmadan önce WithAction(...) zorunlu. Aksi durumda
        // sessizce "unknown" yazmak yerine programcı hatası fail-fast yapılır.
        var ctx = CreateHttpContext();

        var act = () => new ActivityLogBuilder(ctx).Build();

        act.Should().Throw<InvalidOperationException>();
    }
}
