using FluentAssertions;
using Saydin.PriceIngestion.Mappers;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class CoinGeckoMapperTests
{
    private static readonly Guid AssetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly From = new(2021, 1, 1);
    private static readonly DateOnly To = new(2021, 1, 3);

    // Timestamp'ler: 2021-01-01 UTC = 1609459200000 ms (midnight)
    //               2021-01-02 UTC = 1609545600000 ms (midnight)
    //               2021-01-03 UTC = 1609632000000 ms (midnight)
    //               1609459260000 ms = 2021-01-01 00:01 (midnight'tan 60_000 ms uzakta)
    // Yalnız exact midnight kabul edilir; intra-day satır nearest seçimi yapılmadan atlanır.
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
    public void Map_SameDayIntradayNoise_KeepsOnlyExactMidnight()
    {
        // 2021-01-01'de exact midnight kabul edilir; midnight + 60s nearest değildir.
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, From, To);

        result[0].Close.Should().Be(29000.50m);
    }

    [Fact]
    public void Map_IntradaySamples_AreRejected_NotRoundedToNearestDailyPoint()
    {
        // Aynı tarih için 23:55 ve 00:05 girdileri günlük gözlem değildir.
        const string json = """
            {
              "prices": [
                [1609459500000, 100.00],
                [1609545300000, 200.00],
                [1609545900000, 300.00]
              ]
            }
            """;
        // 1609459500000 = 2021-01-01 00:05 UTC
        // 1609545300000 = 2021-01-01 23:55 UTC (same day, far from midnight)
        // 1609545900000 = 2021-01-02 00:05 UTC

        var result = CoinGeckoMapper.Map(json, AssetId,
            new DateOnly(2021, 1, 1), new DateOnly(2021, 1, 2));

        result.Should().BeEmpty();
    }

    [Fact]
    public void Map_MalformedPair_IsPermanentContractFailure()
    {
        var act = () => CoinGeckoMapper.Map(
            """{"prices":[[1609459200000]]}""", AssetId, From, To, "bitcoin");
        act.Should().Throw<Saydin.PriceIngestion.Adapters.ProviderContractException>()
            .Which.Code.Should().Be("contract_invalid_price_pair");
    }

    [Fact]
    public void Map_DuplicateExactDailyObservation_IsPermanentContractFailure()
    {
        var act = () => CoinGeckoMapper.Map(
            """{"prices":[[1609459200000,1],[1609459200000,1]]}""",
            AssetId, From, To, "bitcoin");
        act.Should().Throw<Saydin.PriceIngestion.Adapters.ProviderContractException>()
            .Which.Code.Should().Be("contract_duplicate_daily_observation");
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

    [Fact]
    public void Map_ExactMidnight_ProducesCompleteDailyUtcAuthority()
    {
        var result = CoinGeckoMapper.Map(ValidJson, AssetId, From, To, "bitcoin");

        result.Should().AllSatisfy(point =>
        {
            point.ProviderSource.Should().Be("coingecko");
            point.PriceKind.Should().Be("daily_utc_reference");
            point.IsFinal.Should().BeTrue();
            point.AsOfAt!.Value.TimeOfDay.Should().Be(TimeSpan.Zero);
            point.PayloadSha256.Should().HaveCount(32);
            point.ObservationSha256.Should().BeNull(
                "the database computes canonical observation authority");
            point.SourceObservationId.Should().StartWith("coingecko:bitcoin:try:");
            point.SourceRaw.Should().NotBeNullOrWhiteSpace();
        });
    }
}
