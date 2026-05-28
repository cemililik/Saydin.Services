using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Tests.Adapters;

// Review P1R-004: TwelveDataAdapter — Authorization header'ın gerçekten gönderildiği,
// 429 ve HttpRequestException'ın ExternalApiException'a çevrildiği.
public class TwelveDataAdapterTests
{
    private static readonly DateOnly From = new(2024, 1, 1);
    private static readonly DateOnly To   = new(2024, 1, 3);

    // TwelveData mapper "status":"ok" alanı bekler; bu payload deserialize edilir
    // ama "values" boş olduğu için mapper boş liste döner — adapter HTTP davranışını
    // doğrulamak için yeterli.
    private const string EmptyOkPayload = """
        { "meta": { "symbol": "THYAO" }, "values": [], "status": "ok" }
        """;

    private static (TwelveDataAdapter Adapter, StubHttpMessageHandler Handler) BuildAdapter(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string? apiKey = "test-key")
    {
        var handler = new StubHttpMessageHandler(responder);
        var http    = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.twelvedata.com/"),
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("twelvedata").Returns(http);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalApis:TwelveData:ApiKey"]    = apiKey,
                ["ExternalApis:TwelveData:OutputSize"] = "100",
            })
            .Build();

        var adapter = new TwelveDataAdapter(factory, config, NullLogger<TwelveDataAdapter>.Instance);
        return (adapter, handler);
    }

    [Fact]
    public async Task FetchRange_SendsAuthorizationHeaderWithApiKey()
    {
        // F1.1-5: apikey query param değil HTTP header'da gönderilmeli — trace span'da
        // URL attribute'una secret sızmaması için.
        var (adapter, handler) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EmptyOkPayload),
        });

        await adapter.FetchRangeAsync(Guid.NewGuid(), "THYAO", "THYAO", From, To, default);

        handler.CallCount.Should().Be(1);
        var request = handler.Requests[0];
        request.Headers.TryGetValues("Authorization", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("apikey test-key");
        request.RequestUri!.Query.Should().NotContain("apikey", "API key URL'de görünmemeli");
    }

    [Fact]
    public async Task FetchRange_429_ThrowsExternalApiException()
    {
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var act = () => adapter.FetchRangeAsync(Guid.NewGuid(), "THYAO", "THYAO", From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("twelvedata");
    }

    [Fact]
    public async Task FetchRange_HttpRequestException_ThrowsExternalApiException()
    {
        var (adapter, _) = BuildAdapter(_ => throw new HttpRequestException("network down"));

        var act = () => adapter.FetchRangeAsync(Guid.NewGuid(), "THYAO", "THYAO", From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("twelvedata");
    }

    [Fact]
    public async Task FetchRange_NoApiKey_SkipsRequest()
    {
        // API key yapılandırılmamışsa adapter sessizce skip eder, exception fırlatmaz.
        var (adapter, handler) = BuildAdapter(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            apiKey: null);

        var result = await adapter.FetchRangeAsync(Guid.NewGuid(), "THYAO", "THYAO", From, To, default);

        result.Should().BeEmpty();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task FetchRange_MalformedJson_ThrowsExternalApiException()
    {
        // EVDS adaptörüyle paritede malformed JSON ingestion_jobs success olarak
        // kaybolmamalı; ExternalApiException ile yukarı bildirilir.
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not json"),
        });

        var act = () => adapter.FetchRangeAsync(Guid.NewGuid(), "THYAO", "THYAO", From, To, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("twelvedata");
    }
}
