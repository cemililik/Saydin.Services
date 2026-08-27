using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class TwelveDataAdapterTests
{
    private static readonly DateOnly Day = new(2024, 1, 3);

    [Fact]
    public async Task AuthorizationSecret_IsHeaderOnly()
    {
        var (adapter, handler) = Build(_ => OkPayload());

        var result = await adapter.FetchRangeAsync(Request(), default);

        result.Kind.Should().Be(AdapterOutcomeKind.Data);
        var request = handler.Requests.Single();
        request.Headers.Authorization!.ToString().Should().Be("apikey test-key");
        request.RequestUri!.Query.Should().NotContain("test-key");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, AdapterOutcomeKind.RetryableFailure)]
    [InlineData(HttpStatusCode.Unauthorized, AdapterOutcomeKind.PermanentFailure)]
    [InlineData(HttpStatusCode.InternalServerError, AdapterOutcomeKind.RetryableFailure)]
    public async Task HttpFailure_IsTyped(HttpStatusCode status, AdapterOutcomeKind expected)
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(status));
        (await adapter.FetchRangeAsync(Request(), default)).Kind.Should().Be(expected);
    }

    [Fact]
    public async Task MissingKey_IsPermanentWithoutRequest()
    {
        var (adapter, handler) = Build(_ => OkPayload(), apiKey: null);
        var result = await adapter.FetchRangeAsync(Request(), default);
        result.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        result.Code.Should().Be("auth_missing_api_key");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task EmptyValues_IsHistoricalPartialButCurrentTerminalIsRetryable()
    {
        HttpResponseMessage Empty() => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"ok","meta":{"symbol":"THYAO","interval":"1day","exchange":"BIST","mic_code":"XIST","exchange_timezone":"Europe/Istanbul","currency":"TRY","type":"Common Stock"},"values":[]}"""),
        };
        var (adapter, _) = Build(_ => Empty());
        (await adapter.FetchRangeAsync(Request(), default)).Kind
            .Should().Be(AdapterOutcomeKind.PartialRejected);

        var currentTime = new FakeTimeProvider(
            new DateTimeOffset(2024, 1, 3, 12, 0, 0, TimeSpan.Zero));
        var (currentAdapter, _) = Build(_ => Empty(), timeProvider: currentTime);
        var current = await currentAdapter.FetchRangeAsync(Request(), default);
        current.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        current.Code.Should().Be("not_published_yet");
    }

    [Fact]
    public async Task ProviderCodeAndMessage_AreNotReflectedIntoDetailOrLogs()
    {
        var canaries = new[] { "Authorization", "Bearer", "api_key", "app_id", "credential" };
        var logger = new CaptureLogger<TwelveDataAdapter>();
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"status\":\"error\",\"code\":\"{string.Join(' ', canaries)}\"," +
                $"\"message\":\"{string.Join(' ', canaries)}\"}}"),
        }, logger: logger);

        var result = await adapter.FetchRangeAsync(Request(), default);
        result.Detail.Should().Be("code=unknown");
        foreach (var canary in canaries)
        {
            result.Detail.Should().NotContain(canary);
            logger.Messages.Should().NotContain(message =>
                message.Contains(canary, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task OversizedTransportIsRetryable_AndWrongValueKindIsPermanent()
    {
        var (oversized, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', ProviderTransportLimits.MaxResponseBytes + 1)),
        });
        var size = await oversized.FetchRangeAsync(Request(), default);
        size.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        size.Code.Should().Be("transport_payload_too_large");

        var (wrongKind, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"status":true,"values":[]}"""),
        });
        var kind = await wrongKind.FetchRangeAsync(Request(), default);
        kind.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        kind.Code.Should().Be("contract_value_kind_invalid");
    }

    private static PriceFetchRequest Request() =>
        new(Guid.NewGuid(), "THYAO", "THYAO", Day, Day, new HashSet<DateOnly>());

    private static HttpResponseMessage OkPayload() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"status":"ok","meta":{"symbol":"THYAO","interval":"1day","exchange":"BIST","mic_code":"XIST","exchange_timezone":"Europe/Istanbul","currency":"TRY","type":"Common Stock"},"values":[{"datetime":"2024-01-03","open":"99","high":"102","low":"98","close":"100.5","volume":"1"}]}
            """),
    };

    private static (TwelveDataAdapter, StubHttpMessageHandler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string? apiKey = "test-key",
        ILogger<TwelveDataAdapter>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("twelvedata").Returns(client);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ExternalApis:TwelveData:ApiKey"] = apiKey }).Build();
        return (new TwelveDataAdapter(factory, config, timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<TwelveDataAdapter>.Instance), handler);
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
