using FluentAssertions;
using Saydin.PriceIngestion.Mappers;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class CoinGeckoMapperTests
{
    private static readonly Guid    AssetId  = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly From    = new(2021, 1, 1);
    private static readonly DateOnly To      = new(2021, 1, 3);

    // Timestamp'ler: 2021-01-01 UTC = 1609459200000 ms
    //               2021-01-02 UTC = 1609545600000 ms
    //               2021-01-03 UTC = 1609632000000 ms
    // Aynı gün için iki kayıt: son değer alınmalı (kapanış)
    private const string ValidJson = """
        {
          "prices": [
            [1609459200000, 29000.50],
            [1609459260000, 29100.00],
            [1609545600000, 31000.75],
            [1609632000000, 33000.00]
          ]
        }
        """;

    // ── Temel parse ────────────────────────────────────────────────────────────

    [Fact]
    public void Map_ValidJson_ReturnsPricePointsInDateOrder()
    {
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, From, To);

        result.Should().HaveCount(3);
        result[0].PriceDate.Should().Be(new DateOnly(2021, 1, 1));
        result[1].PriceDate.Should().Be(new DateOnly(2021, 1, 2));
        result[2].PriceDate.Should().Be(new DateOnly(2021, 1, 3));
    }

    [Fact]
    public void Map_ValidJson_AssignsCorrectAssetId()
    {
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, From, To);

        result.Should().AllSatisfy(p => p.AssetId.Should().Be(AssetId));
    }

    [Fact]
    public void Map_SameDayMultipleEntries_KeepsLastValue()
    {
        // 2021-01-01'de iki kayıt var: 29000.50 ve 29100.00 → son değer: 29100.00
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, From, To);

        result[0].Close.Should().Be(29100.00m);
    }

    [Fact]
    public void Map_Jan2Price_CorrectClose()
    {
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, From, To);

        result[1].Close.Should().Be(31000.75m);
    }

    // ── Tarih aralığı filtresi ─────────────────────────────────────────────────

    [Fact]
    public void Map_FiltersOutOfRangeDates()
    {
        // Sadece 2021-01-02 istiyoruz
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, new DateOnly(2021, 1, 2), new DateOnly(2021, 1, 2));

        result.Should().HaveCount(1);
        result[0].PriceDate.Should().Be(new DateOnly(2021, 1, 2));
    }

    // ── Boş / eksik ────────────────────────────────────────────────────────────

    [Fact]
    public void Map_EmptyPricesArray_ReturnsEmptyList()
    {
        const string json = """{"prices": []}""";
        var result = CoinGeckoMapper.Map(json, AssetId, From, To);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Map_AllDatesOutsideRange_ReturnsEmptyList()
    {
        // ValidJson'daki tarihler 2021-01-01/03 — çok daha dar bir aralık istiyoruz
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, new DateOnly(2019, 1, 1), new DateOnly(2019, 12, 31));
        result.Should().BeEmpty();
    }

    // ── Yuvarlama ──────────────────────────────────────────────────────────────

    [Fact]
    public void Map_PriceRounded_ToSixDecimalPlaces()
    {
        const string json = """{"prices": [[1609459200000, 29000.1234567890]]}""";
        var result = CoinGeckoMapper.Map(json, AssetId, From, To);

        result[0].Close.Should().Be(Math.Round(29000.1234567890m, 6, MidpointRounding.AwayFromZero));
    }

    // ── Open/High/Low ─────────────────────────────────────────────────────────

    [Fact]
    public void Map_CoinGeckoResponse_OpenHighLowNull()
    {
        // CoinGecko'nun /coins/{id}/market_chart endpoint'i OHLC değil, sadece prices döner
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, From, To);

        result.Should().AllSatisfy(p =>
        {
            p.Open.Should().BeNull();
            p.High.Should().BeNull();
            p.Low.Should().BeNull();
        });
    }
}
