using FluentAssertions;
using Saydin.PriceIngestion.Mappers;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class GoldApiMapperTests
{
    private static readonly Guid    AssetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateOnly Date   = new(2024, 3, 15);

    // GoldAPI.io yanıt örneği — price_gram_24k TRY cinsinden
    private const string ValidJson = """
        {
          "timestamp": 1710460800,
          "metal": "XAU",
          "currency": "TRY",
          "exchange": "FOREX",
          "symbol": "XAUTRY",
          "prev_close_price": 1990000.00,
          "open_price": 1995000.00,
          "low_price": 1985000.00,
          "high_price": 2005000.00,
          "open_time": 1710374400,
          "price": 2000000.00,
          "ch": 10000.00,
          "chp": 0.50,
          "ask": 2000500.00,
          "bid": 1999500.00,
          "price_gram_24k": 64300.00,
          "price_gram_22k": 58941.00,
          "price_gram_21k": 56263.00,
          "price_gram_18k": 48225.00
        }
        """;

    // ── Temel parse ────────────────────────────────────────────────────────────

    [Fact]
    public void Map_ValidJson_ReturnsPricePoint()
    {
        var result = GoldApiMapper.Map(ValidJson, AssetId, Date);

        result.Should().NotBeNull();
        result!.AssetId.Should().Be(AssetId);
        result.PriceDate.Should().Be(Date);
        result.Close.Should().Be(64300.00m);
    }

    [Fact]
    public void Map_ValidJson_OhlcConvertedFromTroyOunce()
    {
        const decimal troyOunceToGram = 31.1034768m;

        var result = GoldApiMapper.Map(ValidJson, AssetId, Date);

        result.Should().NotBeNull();
        result!.Open.Should().Be(Math.Round(1995000.00m / troyOunceToGram, 6));
        result.High.Should().Be(Math.Round(2005000.00m / troyOunceToGram, 6));
        result.Low.Should().Be(Math.Round(1985000.00m / troyOunceToGram, 6));
    }

    // ── Eksik price_gram_24k ──────────────────────────────────────────────────

    [Fact]
    public void Map_MissingPriceGram24k_ReturnsNull()
    {
        const string json = """
            {
              "open_price": 1995000.00,
              "high_price": 2005000.00,
              "low_price": 1985000.00
            }
            """;

        var result = GoldApiMapper.Map(json, AssetId, Date);
        result.Should().BeNull();
    }

    [Fact]
    public void Map_ZeroPriceGram24k_ReturnsNull()
    {
        const string json = """{"price_gram_24k": 0}""";
        var result = GoldApiMapper.Map(json, AssetId, Date);
        result.Should().BeNull();
    }

    [Fact]
    public void Map_NegativePriceGram24k_ReturnsNull()
    {
        const string json = """{"price_gram_24k": -100}""";
        var result = GoldApiMapper.Map(json, AssetId, Date);
        result.Should().BeNull();
    }

    // ── Opsiyonel OHLC alanları eksik ────────────────────────────────────────

    [Fact]
    public void Map_MissingOhlcFields_OpenHighLowNull()
    {
        const string json = """{"price_gram_24k": 64300.00}""";
        var result = GoldApiMapper.Map(json, AssetId, Date);

        result.Should().NotBeNull();
        result!.Close.Should().Be(64300.00m);
        result.Open.Should().BeNull();
        result.High.Should().BeNull();
        result.Low.Should().BeNull();
    }

    [Fact]
    public void Map_NullOhlcFields_OpenHighLowNull()
    {
        const string json = """
            {
              "price_gram_24k": 64300.00,
              "open_price": null,
              "high_price": null,
              "low_price": null
            }
            """;

        var result = GoldApiMapper.Map(json, AssetId, Date);

        result!.Open.Should().BeNull();
        result.High.Should().BeNull();
        result.Low.Should().BeNull();
    }

    // ── Yuvarlama ──────────────────────────────────────────────────────────────

    [Fact]
    public void Map_CloseRounded_ToSixDecimalPlaces()
    {
        const string json = """{"price_gram_24k": 64300.123456789}""";
        var result = GoldApiMapper.Map(json, AssetId, Date);

        result!.Close.Should().Be(Math.Round(64300.123456789m, 6, MidpointRounding.AwayFromZero));
    }
}
