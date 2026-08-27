using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class CoinGeckoAdapterTests
{
    private static readonly DateOnly Day = new(2024, 1, 1);

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, AdapterOutcomeKind.RetryableFailure)]
    [InlineData(HttpStatusCode.Forbidden, AdapterOutcomeKind.PermanentFailure)]
    [InlineData(HttpStatusCode.InternalServerError, AdapterOutcomeKind.RetryableFailure)]
    public async Task HttpFailure_IsTyped(HttpStatusCode status, AdapterOutcomeKind expected)
    {
        var adapter = Build(_ => new HttpResponseMessage(status));
        (await adapter.FetchRangeAsync(Request(), default)).Kind.Should().Be(expected);
    }

    [Fact]
    public async Task ParseFailure_IsPermanent_NotSuccessEmpty()
    {
        var adapter = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not json"),
        });

        var result = await adapter.FetchRangeAsync(Request(), default);

        result.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        result.Code.Should().Be("parse_error");
    }

    [Fact]
    public async Task IntradayPoint_MakesWindowPartialRejected_AndIsNeverRounded()
    {
        var adapter = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "prices": [
                    [1704067200000, 42000.50],
                    [1704110400000, 42100.50]
                ] }
                """),
        });

        var result = await adapter.FetchRangeAsync(Request(), default);

        result.Kind.Should().Be(AdapterOutcomeKind.PartialRejected);
        result.RawItemCount.Should().Be(2);
        result.Records.Should().ContainSingle();
        result.RejectedCount.Should().Be(1);
    }

    [Fact]
    public async Task TransportPayloadOverLimit_IsRetryableBeforeJsonParse()
    {
        var adapter = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', ProviderTransportLimits.MaxResponseBytes + 1)),
        });

        var result = await adapter.FetchRangeAsync(Request(), default);

        result.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        result.Code.Should().Be("transport_payload_too_large");
    }

    [Fact]
    public async Task WrongPriceValueKind_IsTypedPermanent()
    {
        var adapter = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"prices":[[1704067200000,{"unexpected":true}]]}"""),
        });

        var result = await adapter.FetchRangeAsync(Request(), default);

        result.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        result.Code.Should().Be("contract_value_kind_invalid");
    }

    [Fact]
    public async Task MalformedPriceOutsideRequestedWindow_DoesNotRejectTargetWindow()
    {
        var adapter = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"prices":[[1703980800000,{"bad":true}],[1704067200000,42000.5]]}"""),
        });

        var result = await adapter.FetchRangeAsync(Request(), default);

        result.Kind.Should().Be(AdapterOutcomeKind.Data);
        result.Records.Should().ContainSingle().Which.Close.Should().Be(42000.5m);
    }

    [Fact]
    public async Task ProviderBodyAndNetworkSentinels_AreAbsentFromOutcomeAndLogs()
    {
        const string canary = "provider-secret-canary";
        var logger = new CaptureLogger<CoinGeckoAdapter>();
        var adapter = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{ broken {canary}"),
        }, logger);

        var outcome = await adapter.FetchRangeAsync(Request(), default);

        outcome.Detail.Should().BeNull();
        logger.Messages.Should().NotContain(message => message.Contains(canary, StringComparison.Ordinal));
    }

    private static PriceFetchRequest Request() =>
        new(Guid.NewGuid(), "BTC", "bitcoin", Day, Day, new HashSet<DateOnly>());

    private static CoinGeckoAdapter Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ILogger<CoinGeckoAdapter>? logger = null)
    {
        var client = new HttpClient(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.coingecko.com/api/v3/"),
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("coingecko").Returns(client);
        return new CoinGeckoAdapter(factory, logger ?? NullLogger<CoinGeckoAdapter>.Instance);
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
