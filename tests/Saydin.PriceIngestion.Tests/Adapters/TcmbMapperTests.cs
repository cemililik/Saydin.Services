using FluentAssertions;
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

    // ── USD parse ─────────────────────────────────────────────────────────────

    [Fact]
    public void Map_ValidXml_USD_ReturnsPricePoint()
    {
        var result = TcmbMapper.Map(ValidXml, AssetId, "USD", SampleDate);

        result.Should().NotBeNull();
        result!.AssetId.Should().Be(AssetId);
        result.PriceDate.Should().Be(SampleDate);
        result.Close.Should().Be(5.9518m);
        result.Open.Should().Be(5.9416m);
    }

    [Fact]
    public void Map_ValidXml_EUR_ReturnsPricePoint()
    {
        var result = TcmbMapper.Map(ValidXml, AssetId, "EUR", SampleDate);

        result.Should().NotBeNull();
        result!.Close.Should().Be(6.6660m);
        result.Open.Should().Be(6.6530m);
    }

    // ── Eksik para birimi ────────────────────────────────────────────────────

    [Fact]
    public void Map_UnknownCurrencyCode_ReturnsNull()
    {
        var result = TcmbMapper.Map(ValidXml, AssetId, "JPY", SampleDate);

        result.Should().BeNull();
    }

    // ── ForexSelling yoksa (bozuk XML) ──────────────────────────────────────

    [Fact]
    public void Map_MissingForexSelling_ReturnsNull()
    {
        const string xmlWithoutSelling = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Tarih_Date>
              <Currency CurrencyCode="USD">
                <ForexBuying>5.9416</ForexBuying>
                <ForexSelling></ForexSelling>
              </Currency>
            </Tarih_Date>
            """;

        var result = TcmbMapper.Map(xmlWithoutSelling, AssetId, "USD", SampleDate);

        result.Should().BeNull();
    }

    // ── ForexBuying yoksa Open null olmalı ──────────────────────────────────

    [Fact]
    public void Map_MissingForexBuying_ClosePresentOpenNull()
    {
        const string xmlWithoutBuying = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Tarih_Date>
              <Currency CurrencyCode="USD">
                <ForexBuying></ForexBuying>
                <ForexSelling>5.9518</ForexSelling>
              </Currency>
            </Tarih_Date>
            """;

        var result = TcmbMapper.Map(xmlWithoutBuying, AssetId, "USD", SampleDate);

        result.Should().NotBeNull();
        result!.Close.Should().Be(5.9518m);
        result.Open.Should().BeNull();
    }

    // ── Doğru AssetId ve PriceDate ataması ──────────────────────────────────

    [Fact]
    public void Map_AssignsCorrectAssetIdAndDate()
    {
        var customId = Guid.NewGuid();
        var customDate = new DateOnly(2023, 6, 15);

        var result = TcmbMapper.Map(ValidXml, customId, "USD", customDate);

        result!.AssetId.Should().Be(customId);
        result.PriceDate.Should().Be(customDate);
    }
}
