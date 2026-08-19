using FluentAssertions;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Mappers;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class TcmbMapperTests
{
    private static readonly Guid AssetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateOnly SampleDate = new(2020, 1, 1);

    // ── Gerçeğe yakın TCMB XML örneği ────────────────────────────────────────
    private const string ValidXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Tarih_Date Tarih="01.01.2020" Date="01/01/2020">
          <Currency CrossOrder="0" Kod="USD" CurrencyCode="USD">
            <Unit>1</Unit>
            <Isim>ABD DOLARI</Isim>
            <CurrencyName>US DOLLAR</CurrencyName>
            <ForexBuying>5.9416</ForexBuying>
            <ForexSelling>5.9518</ForexSelling>
            <BanknoteBuying>5.9313</BanknoteBuying>
            <BanknoteSelling>5.9633</BanknoteSelling>
            <CrossRateUSD/>
            <CrossRateOther/>
          </Currency>
          <Currency CrossOrder="1" Kod="EUR" CurrencyCode="EUR">
            <Unit>1</Unit>
            <Isim>EURO</Isim>
            <CurrencyName>EURO</CurrencyName>
            <ForexBuying>6.6530</ForexBuying>
            <ForexSelling>6.6660</ForexSelling>
            <BanknoteBuying>6.6399</BanknoteBuying>
            <BanknoteSelling>6.6813</BanknoteSelling>
            <CrossRateUSD/>
            <CrossRateOther/>
          </Currency>
        </Tarih_Date>
        """;

    // ── F1.4-1: Close = ForexBuying / Unit; Open / High / Low = null ─────────

    [Fact]
    public void Map_ValidXml_USD_ClosePopulatedFromForexBuyingOpenNull()
    {
        var result = TcmbMapper.Map(ValidXml, AssetId, "USD", SampleDate);

        result.Should().NotBeNull();
        result!.AssetId.Should().Be(AssetId);
        result.PriceDate.Should().Be(SampleDate);
        // F1.4-1: TCMB intra-day OHLC yayımlamaz; Close = ForexBuying / Unit
        result.Close.Should().Be(5.9416m);
        result.Open.Should().BeNull();
        result.High.Should().BeNull();
        result.Low.Should().BeNull();
    }

    [Fact]
    public void Map_ValidXml_EUR_ClosePopulatedFromForexBuying()
    {
        var result = TcmbMapper.Map(ValidXml, AssetId, "EUR", SampleDate);

        result.Should().NotBeNull();
        result!.Close.Should().Be(6.6530m);
        result.Open.Should().BeNull();
    }

    // ── Eksik para birimi ────────────────────────────────────────────────────

    [Fact]
    public void Map_UnknownCurrencyCode_ReturnsNull()
    {
        var result = TcmbMapper.Map(ValidXml, AssetId, "JPY", SampleDate);

        result.Should().BeNull();
    }

    // ── ForexBuying yoksa (bozuk XML) ───────────────────────────────────────

    [Fact]
    public void Map_MissingForexBuying_ReturnsNull()
    {
        const string xmlWithoutBuying = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Tarih_Date Tarih="01.01.2020" Date="01/01/2020">
              <Currency CurrencyCode="USD">
                <ForexBuying></ForexBuying>
                <ForexSelling>5.9518</ForexSelling>
              </Currency>
            </Tarih_Date>
            """;

        var result = TcmbMapper.Map(xmlWithoutBuying, AssetId, "USD", SampleDate);

        result.Should().BeNull();
    }

    // ── Doğru AssetId ve PriceDate ataması ──────────────────────────────────

    [Fact]
    public void Map_RequestDateDifferentFromPayloadDate_IsRejected()
    {
        var customId = Guid.NewGuid();
        var customDate = new DateOnly(2023, 6, 15);

        var act = () => TcmbMapper.Map(ValidXml, customId, "USD", customDate);

        act.Should().Throw<ProviderContractException>()
            .Which.Code.Should().Be("contract_observation_date_mismatch");
    }

    // ── Unit normalizasyonu ─────────────────────────────────────────────────
    // TCMB JPY/KRW/IDR gibi düşük-değerli para birimlerini 100 birim üzerinden
    // kote eder; mapper bunu Unit'e bölerek "1 birim X kaç TL" formatına çevirmeli.

    private const string XmlWithJpyUnit100 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Tarih_Date Tarih="01.01.2020" Date="01/01/2020">
          <Currency CrossOrder="5" Kod="JPY" CurrencyCode="JPY">
            <Unit>100</Unit>
            <Isim>JAPON YENİ</Isim>
            <CurrencyName>JAPENESE YEN</CurrencyName>
            <ForexBuying>5.4730</ForexBuying>
            <ForexSelling>5.5121</ForexSelling>
            <BanknoteBuying>5.4368</BanknoteBuying>
            <BanknoteSelling>5.5524</BanknoteSelling>
          </Currency>
        </Tarih_Date>
        """;

    [Fact]
    public void Map_JpyWithUnit100_NormalizesPricePerUnit()
    {
        var result = TcmbMapper.Map(XmlWithJpyUnit100, AssetId, "JPY", SampleDate);

        result.Should().NotBeNull();
        // ForexBuying 5.4730 / 100 = 0.054730 (1 JPY ≈ 0.055 TL)
        result!.Close.Should().Be(0.054730m);
        result.Open.Should().BeNull();
    }

    [Fact]
    public void Map_MissingUnit_DefaultsToOne()
    {
        const string xmlWithoutUnit = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Tarih_Date Tarih="01.01.2020" Date="01/01/2020">
              <Currency CurrencyCode="USD">
                <ForexBuying>5.9416</ForexBuying>
                <ForexSelling>5.9518</ForexSelling>
              </Currency>
            </Tarih_Date>
            """;

        var result = TcmbMapper.Map(xmlWithoutUnit, AssetId, "USD", SampleDate);

        result.Should().NotBeNull();
        result!.Close.Should().Be(5.9416m);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-100")]
    public void Map_ZeroOrNegativeUnit_IsPermanentContractFailure(string unitValue)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Tarih_Date Tarih="01.01.2020" Date="01/01/2020">
              <Currency CurrencyCode="USD">
                <Unit>{unitValue}</Unit>
                <ForexBuying>5.9416</ForexBuying>
                <ForexSelling>5.9518</ForexSelling>
              </Currency>
            </Tarih_Date>
            """;

        var act = () => TcmbMapper.Map(xml, AssetId, "USD", SampleDate);
        act.Should().Throw<ProviderContractException>()
            .Which.Code.Should().Be("contract_unit_invalid");
    }

    [Fact]
    public void Map_NonPositiveForexBuying_IsPermanentContractFailure()
    {
        const string xml = """
            <Tarih_Date Tarih="01.01.2020" Date="01/01/2020">
              <Currency CurrencyCode="USD"><Unit>1</Unit><ForexBuying>-1</ForexBuying></Currency>
            </Tarih_Date>
            """;
        var act = () => TcmbMapper.Map(xml, AssetId, "USD", SampleDate);
        act.Should().Throw<ProviderContractException>()
            .Which.Code.Should().Be("contract_price_invalid");
    }

    // ── F1.1-2: MapMany — gün-bazlı dedup için tüm semboller tek XML'den ─────

    [Fact]
    public void MapMany_ValidXml_AllSymbolsParsedFromSameDoc()
    {
        var usdId = Guid.NewGuid();
        var eurId = Guid.NewGuid();
        var map = new Dictionary<string, Guid> { ["USD"] = usdId, ["EUR"] = eurId };

        var result = TcmbMapper.MapMany(ValidXml, map, SampleDate);

        result.Should().HaveCount(2);
        result.Should().Contain(p => p.AssetId == usdId && p.Close == 5.9416m);
        result.Should().Contain(p => p.AssetId == eurId && p.Close == 6.6530m);
    }

    [Fact]
    public void MapMany_MissingCurrency_SkipsIt()
    {
        var usdId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        var map = new Dictionary<string, Guid> { ["USD"] = usdId, ["XYZ"] = unknownId };

        var result = TcmbMapper.MapMany(ValidXml, map, SampleDate);

        result.Should().HaveCount(1);
        result[0].AssetId.Should().Be(usdId);
    }
}
