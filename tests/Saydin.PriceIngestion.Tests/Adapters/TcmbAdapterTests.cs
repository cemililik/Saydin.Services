using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Tests.Adapters;

// Review P1R-004: Faz 1 TcmbAdapter davranış değişikliği (day-level cache hit/miss,
// HttpRequestException → ExternalApiException, 404 → null, parse-once XDocument)
// burada `HttpMessageHandler` stub'ı ile doğrulanır.
public class TcmbAdapterTests
{
    private const string SampleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Tarih_Date Tarih="03.01.2024" Date="01/03/2024">
          <Currency CrossOrder="0" Kod="USD" CurrencyCode="USD">
            <Unit>1</Unit>
            <Isim>ABD DOLARI</Isim>
            <CurrencyName>US DOLLAR</CurrencyName>
            <ForexBuying>30.0000</ForexBuying>
            <ForexSelling>30.1000</ForexSelling>
          </Currency>
          <Currency CrossOrder="1" Kod="EUR" CurrencyCode="EUR">
            <Unit>1</Unit>
            <Isim>EURO</Isim>
            <CurrencyName>EURO</CurrencyName>
            <ForexBuying>32.5000</ForexBuying>
            <ForexSelling>32.6000</ForexSelling>
          </Currency>
        </Tarih_Date>
        """;

    private static readonly DateOnly TestDate = new(2024, 1, 3);   // Çarşamba — TCMB yayını var.

    private static (TcmbAdapter Adapter, StubHttpMessageHandler Handler) BuildAdapter(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        var http    = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.tcmb.gov.tr/kurlar/"),
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("tcmb").Returns(http);

        var adapter = new TcmbAdapter(factory, NullLogger<TcmbAdapter>.Instance);
        return (adapter, handler);
    }

    [Fact]
    public async Task FetchRange_SameDayTwoSymbols_ReusesXmlCache_OneHttpCall()
    {
        // F1.1-2 / P1R-002: aynı tarih için ikinci sembol fetch'i HTTP'ye gitmez,
        // XDocument cache'inden okur.
        var (adapter, handler) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleXml),
        });

        var usdAssetId = Guid.NewGuid();
        var eurAssetId = Guid.NewGuid();

        var usdResult = await adapter.FetchRangeAsync(usdAssetId, "USDTRY", "USD", TestDate, TestDate, default);
        var eurResult = await adapter.FetchRangeAsync(eurAssetId, "EURTRY", "EUR", TestDate, TestDate, default);

        usdResult.Should().HaveCount(1);
        usdResult[0].Close.Should().Be(30.0000m);
        eurResult.Should().HaveCount(1);
        eurResult[0].Close.Should().Be(32.5000m);

        handler.CallCount.Should().Be(1, "aynı tarih için XML cache'lenmeli");
    }

    [Fact]
    public async Task FetchRange_NotFound_ReturnsEmpty()
    {
        // TCMB resmi tatil → 404 → null PricePoint (boş liste).
        var (adapter, handler) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await adapter.FetchRangeAsync(Guid.NewGuid(), "USDTRY", "USD", TestDate, TestDate, default);

        result.Should().BeEmpty();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task FetchRange_HttpRequestException_ThrowsExternalApiException()
    {
        // Polly retry tükendiğinde adapter ExternalApiException ile sızdırır,
        // worker ingestion_jobs failed kaydı atabilsin diye (F1.1-3).
        var (adapter, _) = BuildAdapter(_ => throw new HttpRequestException("network down"));

        var act = () => adapter.FetchRangeAsync(Guid.NewGuid(), "USDTRY", "USD", TestDate, TestDate, default);

        var ex = await act.Should().ThrowAsync<ExternalApiException>();
        ex.Which.ApiSource.Should().Be("tcmb");
    }

    [Fact]
    public async Task FetchRange_MalformedXml_ReturnsEmptyAndDoesNotThrow()
    {
        // F1.1-3: bozuk XML tek-gün "veri yok" olarak yumuşatılır, range fail değil.
        var (adapter, _) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<not-valid-xml"),
        });

        var result = await adapter.FetchRangeAsync(Guid.NewGuid(), "USDTRY", "USD", TestDate, TestDate, default);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchRange_SkipsWeekendDays()
    {
        // TCMB hafta sonu yayın yapmaz; adapter Cumartesi/Pazar günleri HTTP isteği atmaz.
        var saturday = new DateOnly(2024, 1, 6);
        var sunday   = new DateOnly(2024, 1, 7);
        var (adapter, handler) = BuildAdapter(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleXml),
        });

        var result = await adapter.FetchRangeAsync(
            Guid.NewGuid(), "USDTRY", "USD", saturday, sunday, default);

        result.Should().BeEmpty();
        handler.CallCount.Should().Be(0);
    }
}
