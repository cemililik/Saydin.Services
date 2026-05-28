using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Tests.Adapters;

// Review P1R-010 / F1.1-7: EvdsInflationAdapter HTTP davranışı — 5xx, 401/403,
// 429 ve network hataları artık sessizce return [] yapmıyor, ExternalApiException
// fırlatıyor. Adapter ayrıca `key` header'ını query string'e koymadan göndermeli.
public class EvdsInflationAdapterTests
{
    private static readonly DateOnly From = new(2024, 1, 1);
    private static readonly DateOnly To   = new(2024, 3, 1);

    private const string ValidPayload = """
        {
          "totalCount": 0,
          "items": []
        }
        """;

    private static (EvdsInflationAdapter Adapter, StubHttpMessageHandler Handler) BuildAdapter(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string? apiKey = "test-key")
    {
        var handler = new StubHttpMessageHandler(responder);
        var http    = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://evds3.tcmb.gov.tr/"),
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("evds").Returns(http);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalApis:Evds:ApiKey"] = apiKey,
            })
            .Build();

        var adapter = new EvdsInflationAdapter(factory, config, NullLogger<EvdsInflationAdapter>.Instance);
        return (adapter, handler);
    }

    [Fact]
    public async Task FetchRange_SendsApiKeyAsHeader_NotInQuery()
    {
        var (adapter, handler) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidPayload),
        });

        await adapter.FetchRangeAsync(From, To, default);

        handler.CallCount.Should().Be(1);
        var request = handler.Requests[0];
        request.Headers.TryGetValues("key", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("test-key");
        request.RequestUri!.Query.Should().NotContain("key=", "API key URL'de görünmemeli");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FetchRange_HttpServerError_ThrowsExternalApiException(HttpStatusCode statusCode)
    {
        // F1.1-7: 5xx artık return [] ile yutulmaz — worker ingestion_jobs failed
        // kaydı atabilsin diye ExternalApiException sızdırılır.
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("server error"),
        });

        var act = () => adapter.FetchRangeAsync(From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("evds");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchRange_AuthError_ThrowsExternalApiException(HttpStatusCode statusCode)
    {
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(statusCode));

        var act = () => adapter.FetchRangeAsync(From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("evds");
    }

    [Fact]
    public async Task FetchRange_429_ThrowsExternalApiException()
    {
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var act = () => adapter.FetchRangeAsync(From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("evds");
    }

    [Fact]
    public async Task FetchRange_HttpRequestException_ThrowsExternalApiException()
    {
        var (adapter, _) = BuildAdapter(_ => throw new HttpRequestException("network down"));

        var act = () => adapter.FetchRangeAsync(From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("evds");
    }

    [Fact]
    public async Task FetchRange_MalformedJson_ThrowsExternalApiException()
    {
        // EVDS için bozuk JSON da silent değil — ExternalApiException ile yukarı fırlar
        // (CoinGecko'dan farklı: EVDS sözleşmesi tam olmalı).
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not json"),
        });

        var act = () => adapter.FetchRangeAsync(From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("evds");
    }

    [Fact]
    public async Task FetchRange_NoApiKey_SkipsAndReturnsEmpty()
    {
        var (adapter, handler) = BuildAdapter(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            apiKey: null);

        var result = await adapter.FetchRangeAsync(From, To, default);

        result.Should().BeEmpty();
        handler.CallCount.Should().Be(0);
    }
}
