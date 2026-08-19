using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class OpenExchangeRatesAdapterTests
{
    private static readonly DateOnly Day = new(2024, 1, 3);
    private static readonly TimeProvider Clock =
        new FixedTimeProvider(new DateTimeOffset(2024, 1, 5, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task AppId_IsTokenHeader_NotUrlOrQuery()
    {
        var (adapter, handler) = Build(_ => ValidResponse());
        var result = await adapter.FetchRangeAsync(Request(), default);

        result.Kind.Should().Be(AdapterOutcomeKind.Data);
        var request = handler.Requests.Single();
        request.Headers.Authorization!.Scheme.Should().Be("Token");
        request.Headers.Authorization.Parameter.Should().Be("secret-app-id");
        request.RequestUri!.ToString().Should().NotContain("secret-app-id");
        request.RequestUri.Query.Should().NotContain("app_id");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, AdapterOutcomeKind.PermanentFailure, "historical_route_not_found")]
    [InlineData(HttpStatusCode.BadRequest, AdapterOutcomeKind.PermanentFailure, "invalid_or_unavailable_date")]
    [InlineData(HttpStatusCode.Unauthorized, AdapterOutcomeKind.PermanentFailure, "auth_rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, AdapterOutcomeKind.RetryableFailure, "http_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, AdapterOutcomeKind.RetryableFailure, "http_5xx")]
    public async Task HttpStatus_IsTyped_NotExpectedNoData(
        HttpStatusCode status, AdapterOutcomeKind expected, string code)
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(status));
        var result = await adapter.FetchRangeAsync(Request(), default);
        result.Kind.Should().Be(expected);
        result.Code.Should().Be(code);
    }

    [Fact]
    public async Task MissingRate_IsPermanentContractFailure()
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"base":"USD","timestamp":1704240000,"rates":{"TRY":30.0}}"""),
        });
        (await adapter.FetchRangeAsync(Request(), default)).Code.Should().Be("contract_missing_rate");
    }

    [Theory]
    [InlineData("EUR", 1704240000, "contract_identity_mismatch")]
    [InlineData("USD", 1704326400, "contract_observation_date_mismatch")]
    public async Task ResponseBaseAndDate_AreBoundToRequestIdentity(
        string baseCurrency, long timestamp, string code)
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"base\":\"{baseCurrency}\",\"timestamp\":{timestamp},\"rates\":{{\"XAU\":0.0005,\"TRY\":30}}}}"),
        });
        var result = await adapter.FetchRangeAsync(Request(), default);
        result.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        result.Code.Should().Be(code);
    }

    [Fact]
    public async Task CurrentUtcDay_IsProvisionalWithoutHttp()
    {
        var (adapter, handler) = Build(_ => ValidResponse());
        var today = new DateOnly(2024, 1, 5);
        var result = await adapter.FetchRangeAsync(new PriceFetchRequest(
            Guid.NewGuid(), "XAU", "XAU", today, today, new HashSet<DateOnly>()), default);
        result.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        result.Code.Should().Be("current_day_provisional");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlanNotAllowed429_IsPermanent_WhileGeneric429IsRetryable()
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"message":"not_allowed"}"""),
        });
        var result = await adapter.FetchRangeAsync(Request(), default);
        result.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        result.Code.Should().Be("plan_not_allowed");
    }

    [Fact]
    public async Task ForbiddenBodySecret_IsNotLoggedOrReturned()
    {
        var canaries = new[] { "Authorization", "Bearer", "api_key", "app_id", "credential" };
        var logger = new CaptureLogger<OpenExchangeRatesAdapter>();
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                $"{{\"message\":\"access_restricted {string.Join(' ', canaries)}\"}}"),
        }, logger: logger);
        var result = await adapter.FetchRangeAsync(Request(), default);
        result.Code.Should().Be("access_restricted");
        foreach (var canary in canaries)
        {
            result.Detail.Should().NotContain(canary);
            logger.Messages.Should().NotContain(message =>
                message.Contains(canary, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task MalformedJsonAndMissingRates_ArePermanent()
    {
        var (malformed, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ broken"),
        });
        (await malformed.FetchRangeAsync(Request(), default)).Code.Should().Be("parse_error");

        var (missing, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"base":"USD","timestamp":1704240000,"status":"ok"}"""),
        });
        (await missing.FetchRangeAsync(Request(), default)).Code.Should().Be("contract_missing_rate");
    }

    [Fact]
    public async Task NetworkException_DoesNotLeakMessageIntoOutcomeOrLog()
    {
        const string canary = "network-secret-canary";
        var logger = new CaptureLogger<OpenExchangeRatesAdapter>();
        var (adapter, _) = Build(_ => throw new HttpRequestException(canary), logger: logger);
        var result = await adapter.FetchRangeAsync(Request(), default);
        result.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        result.Detail.Should().BeNull();
        logger.Messages.Should().NotContain(message => message.Contains(canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondDayMissingRate_ReturnsPartialRejectedForWholeWindow()
    {
        var calls = 0;
        var (adapter, _) = Build(_ =>
        {
            calls++;
            return calls == 1 ? ValidResponse() : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"base":"USD","timestamp":1704326400,"rates":{"TRY":30.0}}"""),
            };
        });
        var request = new PriceFetchRequest(Guid.NewGuid(), "XAU", "XAU",
            Day, Day.AddDays(1), new HashSet<DateOnly>());
        var result = await adapter.FetchRangeAsync(request, default);
        result.Kind.Should().Be(AdapterOutcomeKind.PartialRejected);
        result.Records.Should().ContainSingle();
        result.RejectedCount.Should().Be(1);
    }

    [Fact]
    public async Task MissingAppId_IsPermanentAndDoesNotCallProvider()
    {
        var (adapter, handler) = Build(_ => ValidResponse(), appId: null);
        var result = await adapter.FetchRangeAsync(Request(), default);
        result.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        result.Code.Should().Be("auth_missing_app_id");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task CachedMetal_CompletesWithoutHttpOrDelay()
    {
        var time = new FakeTimeProvider(Clock.GetUtcNow());
        var (adapter, handler) = Build(_ => ValidResponse(), timeProvider: time);
        (await adapter.FetchRangeAsync(Request(), default)).Kind.Should().Be(AdapterOutcomeKind.Data);
        using var cancellation = new CancellationTokenSource();

        var cached = adapter.FetchRangeAsync(
            new PriceFetchRequest(Guid.NewGuid(), "XAG", "XAG", Day, Day, new HashSet<DateOnly>()),
            cancellation.Token);
        var completedWithoutDelay = cached.IsCompleted;
        if (!completedWithoutDelay)
        {
            cancellation.Cancel();
            var cancelled = async () => await cached;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
        }

        completedWithoutDelay.Should().BeTrue("a cache hit has no provider pacing work");
        (await cached).Kind.Should().Be(AdapterOutcomeKind.Data);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RemoteRequests_AreDelayedOnlyBetweenWireCalls()
    {
        var time = new FakeTimeProvider(Clock.GetUtcNow());
        var calls = 0;
        var (adapter, handler) = Build(_ =>
        {
            calls++;
            return ValidResponse(calls == 1 ? 1704240000 : 1704326400);
        }, timeProvider: time);
        var request = new PriceFetchRequest(Guid.NewGuid(), "XAU", "XAU",
            Day, Day.AddDays(1), new HashSet<DateOnly>());
        using var cancellation = new CancellationTokenSource();

        var fetch = adapter.FetchRangeAsync(request, cancellation.Token);
        try
        {
            handler.CallCount.Should().Be(1);
            fetch.IsCompleted.Should().BeFalse();
            time.Advance(TimeSpan.FromMilliseconds(199));
            handler.CallCount.Should().Be(1);
            time.Advance(TimeSpan.FromMilliseconds(1));
            handler.CallCount.Should().Be(2);

            (await fetch).Kind.Should().Be(AdapterOutcomeKind.Data);
            handler.CallCount.Should().Be(2);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task CancellationDuringInterRemoteDelay_StopsBeforeSecondHttp()
    {
        var time = new FakeTimeProvider(Clock.GetUtcNow());
        var (adapter, handler) = Build(_ => ValidResponse(), timeProvider: time);
        var request = new PriceFetchRequest(Guid.NewGuid(), "XAU", "XAU",
            Day, Day.AddDays(1), new HashSet<DateOnly>());
        using var cancellation = new CancellationTokenSource();

        var fetch = adapter.FetchRangeAsync(request, cancellation.Token);
        handler.CallCount.Should().Be(1);
        cancellation.Cancel();

        var cancelled = async () => await fetch;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().Be(1);
    }

    private static PriceFetchRequest Request() =>
        new(Guid.NewGuid(), "XAU", "XAU", Day, Day, new HashSet<DateOnly>());

    private static HttpResponseMessage ValidResponse(long timestamp = 1704240000) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"base\":\"USD\",\"timestamp\":{timestamp},\"rates\":{{\"XAU\":0.0005,\"XAG\":0.02,\"TRY\":30.0}}}}"),
        };

    private static (OpenExchangeRatesAdapter, StubHttpMessageHandler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? appId = "secret-app-id",
        ILogger<OpenExchangeRatesAdapter>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openexchangerates.org/api/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("openexchangerates").Returns(client);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            { ["ExternalApis:OpenExchangeRates:AppId"] = appId }).Build();
        return (new OpenExchangeRatesAdapter(factory, config, timeProvider ?? Clock,
            logger ?? NullLogger<OpenExchangeRatesAdapter>.Instance), handler);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
