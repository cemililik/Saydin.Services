using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Tests.Adapters;

// Review P1R-004: CoinGeckoAdapter HTTP davranışı: 429/403 → ExternalApiException
// (F1.1-4), JsonException → empty list, HttpRequestException → ExternalApiException.
public class CoinGeckoAdapterTests
{
    private static readonly DateOnly From = new(2024, 1, 1);
    private static readonly DateOnly To   = new(2024, 1, 3);

    private const string ValidJsonPayload = """
        { "prices": [ [1704067200000, 42000.50] ] }
        """;

    private static (CoinGeckoAdapter Adapter, StubHttpMessageHandler Handler) BuildAdapter(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        var http    = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.coingecko.com/api/v3/"),
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("coingecko").Returns(http);

        var adapter = new CoinGeckoAdapter(factory, NullLogger<CoinGeckoAdapter>.Instance);
        return (adapter, handler);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchRange_RateLimitOrForbidden_ThrowsExternalApiExceptionAsync(HttpStatusCode statusCode)
    {
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(statusCode));

        var act = () => adapter.FetchRangeAsync(Guid.NewGuid(), "BTC", "bitcoin", From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("coingecko");
    }

    [Fact]
    public async Task FetchRange_MalformedJson_ThrowsExternalApiExceptionAsync()
    {
        // Bozuk JSON sessizce "veri yok" olarak yumuşatılmaz; EVDS adaptörüyle
        // paritede ExternalApiException fırlar — upstream contract değişiklikleri
        // ingestion_jobs failed olarak görünmeli, success olarak kaybolmamalı.
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not json"),
        });

        var act = () => adapter.FetchRangeAsync(Guid.NewGuid(), "BTC", "bitcoin", From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("coingecko");
    }

    [Fact]
    public async Task FetchRange_HttpRequestException_ThrowsExternalApiExceptionAsync()
    {
        var (adapter, _) = BuildAdapter(_ => throw new HttpRequestException("network down"));

        var act = () => adapter.FetchRangeAsync(Guid.NewGuid(), "BTC", "bitcoin", From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("coingecko");
    }

    [Fact]
    public async Task FetchRange_ValidResponse_ReturnsPricePointsAsync()
    {
        var (adapter, handler) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidJsonPayload),
        });

        var result = await adapter.FetchRangeAsync(Guid.NewGuid(), "BTC", "bitcoin", From, To, default);

        result.Should().NotBeEmpty();
        handler.CallCount.Should().Be(1);
    }
}
