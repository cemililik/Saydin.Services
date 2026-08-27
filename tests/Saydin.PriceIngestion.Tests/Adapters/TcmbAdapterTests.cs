using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class TcmbAdapterTests
{
    private static readonly DateOnly Weekday = new(2024, 1, 3);
    private const string Xml = """
        <Tarih_Date Tarih="03.01.2024" Date="01/03/2024"><Currency CurrencyCode="USD"><Unit>1</Unit><ForexBuying>30.0</ForexBuying></Currency></Tarih_Date>
        """;

    [Fact]
    public async Task SameDayTwoAssets_SharesOneHttpBody()
    {
        var (adapter, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Xml),
        });

        var first = await adapter.FetchRangeAsync(Request(Guid.NewGuid()), default);
        var second = await adapter.FetchRangeAsync(Request(Guid.NewGuid()), default);

        first.Kind.Should().Be(AdapterOutcomeKind.Data);
        second.Kind.Should().Be(AdapterOutcomeKind.Data);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Weekday404_IsHistoricalPermanentButCurrentPublicationIsRetryable()
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await adapter.FetchRangeAsync(Request(Guid.NewGuid()), default);
        result.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        result.Code.Should().Be("unexpected_404");

        var currentTime = new FakeTimeProvider(
            new DateTimeOffset(2024, 1, 3, 14, 0, 0, TimeSpan.Zero));
        var (currentAdapter, _) = Build(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound), currentTime);
        var current = await currentAdapter.FetchRangeAsync(Request(Guid.NewGuid()), default);
        current.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        current.Code.Should().Be("provider_publication_pending");
    }

    [Fact]
    public async Task ConsecutiveWeekday404_CannotBecomeSuccessZero()
    {
        var (adapter, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var request = new PriceFetchRequest(Guid.NewGuid(), "USDTRY", "USD",
            new(2024, 1, 3), new(2024, 1, 5), new HashSet<DateOnly>());
        var result = await adapter.FetchRangeAsync(request, default);
        result.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task KnownWeekend_IsExplicitExpectedNoDataWithoutHttp()
    {
        var weekend = new DateOnly(2024, 1, 6);
        var (adapter, handler) = Build(_ => throw new InvalidOperationException());
        var result = await adapter.FetchRangeAsync(new PriceFetchRequest(
            Guid.NewGuid(), "USDTRY", "USD", weekend, weekend,
            new HashSet<DateOnly> { weekend }), default);
        result.Kind.Should().Be(AdapterOutcomeKind.ExpectedNoData);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MalformedXml_IsPermanent()
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<broken"),
        });
        (await adapter.FetchRangeAsync(Request(Guid.NewGuid()), default)).Kind
            .Should().Be(AdapterOutcomeKind.PermanentFailure);
    }

    [Fact]
    public async Task OversizedTransportPayload_IsTypedRetryable()
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', ProviderTransportLimits.MaxResponseBytes + 1)),
        });

        var result = await adapter.FetchRangeAsync(Request(Guid.NewGuid()), default);

        result.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        result.Code.Should().Be("transport_payload_too_large");
    }

    [Theory]
    [InlineData("TP..USD", "USD")]
    [InlineData("TP.DK..A", "")]
    public async Task CurrencyExtraction_MatchesDatabaseSplitPart(
        string sourceId, string expectedCurrency)
    {
        var xml = $"""
            <Tarih_Date Tarih="03.01.2024" Date="01/03/2024"><Currency CurrencyCode="{expectedCurrency}"><Unit>1</Unit><ForexBuying>30.0</ForexBuying></Currency></Tarih_Date>
            """;
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        });
        var request = new PriceFetchRequest(Guid.NewGuid(), "USDTRY", sourceId,
            Weekday, Weekday, new HashSet<DateOnly>());

        var result = await adapter.FetchRangeAsync(request, default);

        result.Kind.Should().Be(AdapterOutcomeKind.Data);
    }

    [Fact]
    public async Task CacheEntryExpiresAgainstInjectedClock()
    {
        var time = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));
        var (adapter, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Xml),
        }, time);

        await adapter.FetchRangeAsync(Request(Guid.NewGuid()), default);
        time.Advance(TimeSpan.FromMinutes(61));
        await adapter.FetchRangeAsync(Request(Guid.NewGuid()), default);

        handler.CallCount.Should().Be(2);
    }

    private static PriceFetchRequest Request(Guid assetId) =>
        new(assetId, "USDTRY", "USD", Weekday, Weekday, new HashSet<DateOnly>());

    private static (TcmbAdapter, StubHttpMessageHandler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        TimeProvider? timeProvider = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.tcmb.gov.tr/kurlar/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("tcmb").Returns(client);
        return (new TcmbAdapter(
            factory, NullLogger<TcmbAdapter>.Instance,
            timeProvider ?? TimeProvider.System), handler);
    }
}
