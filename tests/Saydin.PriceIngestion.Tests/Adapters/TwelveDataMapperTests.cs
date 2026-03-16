using FluentAssertions;
using Saydin.PriceIngestion.Mappers;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class TwelveDataMapperTests
{
    private static readonly Guid AssetId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // Twelve Data /time_series yanıt örneği (günlük OHLCV)
    private const string ValidJson = """
        {
          "meta": {
            "symbol": "AKBNK",
            "interval": "1day",
            "currency": "TRY",
            "exchange": "BIST"
          },
          "values": [
            {
              "datetime": "2024-03-15",
              "open": "47.12",
              "high": "48.50",
              "low": "46.90",
              "close": "48.10",
              "volume": "15234567"
            },
            {
              "datetime": "2024-03-14",
              "open": "45.80",
              "high": "47.20",
              "low": "45.50",
              "close": "47.12",
              "volume": "12000000"
            }
          ],
          "status": "ok"
        }
        """;

    // ── Temel parse ────────────────────────────────────────────────────────────

    [Fact]
    public void Map_ValidJson_ReturnsPricePointsInDateOrder()
    {
        var result = TwelveDataMapper.Map(ValidJson, AssetId);

        result.Should().HaveCount(2);
        result[0].PriceDate.Should().Be(new DateOnly(2024, 3, 14)); // eski → yeni sıralama
        result[1].PriceDate.Should().Be(new DateOnly(2024, 3, 15));
    }

    [Fact]
    public void Map_ValidJson_OhlcvParsedCorrectly()
    {
        var result = TwelveDataMapper.Map(ValidJson, AssetId);
        var latest = result[1]; // 2024-03-15

        latest.AssetId.Should().Be(AssetId);
        latest.Close.Should().Be(48.10m);
        latest.Open.Should().Be(47.12m);
        latest.High.Should().Be(48.50m);
        latest.Low.Should().Be(46.90m);
        latest.Volume.Should().Be(15234567m);
    }

    // ── status != "ok" ────────────────────────────────────────────────────────

    [Fact]
    public void Map_StatusError_ReturnsEmptyList()
    {
        const string json = """
            {
              "status": "error",
              "code": 404,
              "message": "**symbol** not found: YOKHISSE"
            }
            """;

        var result = TwelveDataMapper.Map(json, AssetId);
        result.Should().BeEmpty();
    }

    // ── Eksik values ──────────────────────────────────────────────────────────

    [Fact]
    public void Map_MissingValuesProperty_ReturnsEmptyList()
    {
        const string json = """{"status": "ok"}""";
        var result = TwelveDataMapper.Map(json, AssetId);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Map_EmptyValuesArray_ReturnsEmptyList()
    {
        const string json = """{"status": "ok", "values": []}""";
        var result = TwelveDataMapper.Map(json, AssetId);
        result.Should().BeEmpty();
    }

    // ── Bozuk kayıt (kısmi skip) ──────────────────────────────────────────────

    [Fact]
    public void Map_InvalidDatetime_SkipsEntry()
    {
        const string json = """
            {
              "status": "ok",
              "values": [
                {"datetime": "BOZUK", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00"},
                {"datetime": "2024-03-15", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00"}
              ]
            }
            """;

        var result = TwelveDataMapper.Map(json, AssetId);
        result.Should().HaveCount(1);
        result[0].PriceDate.Should().Be(new DateOnly(2024, 3, 15));
    }

    [Fact]
    public void Map_InvalidClose_SkipsEntry()
    {
        const string json = """
            {
              "status": "ok",
              "values": [
                {"datetime": "2024-03-14", "close": "N/A", "open": "47.00", "high": "49.00", "low": "46.00"},
                {"datetime": "2024-03-15", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00"}
              ]
            }
            """;

        var result = TwelveDataMapper.Map(json, AssetId);
        result.Should().HaveCount(1);
        result[0].PriceDate.Should().Be(new DateOnly(2024, 3, 15));
    }

    // ── Opsiyonel alanlar ────────────────────────────────────────────────────

    [Fact]
    public void Map_MissingVolume_VolumeNull()
    {
        const string json = """
            {
              "status": "ok",
              "values": [
                {"datetime": "2024-03-15", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00"}
              ]
            }
            """;

        var result = TwelveDataMapper.Map(json, AssetId);
        result[0].Volume.Should().BeNull();
    }

    [Fact]
    public void Map_MissingOpenHighLow_ThoseFieldsNull()
    {
        const string json = """
            {
              "status": "ok",
              "values": [
                {"datetime": "2024-03-15", "close": "48.10"}
              ]
            }
            """;

        var result = TwelveDataMapper.Map(json, AssetId);
        result[0].Open.Should().BeNull();
        result[0].High.Should().BeNull();
        result[0].Low.Should().BeNull();
        result[0].Close.Should().Be(48.10m);
    }

    // ── Status yok ama values var (toleranslı) ───────────────────────────────

    [Fact]
    public void Map_NoStatusField_ParsesSuccessfully()
    {
        const string json = """
            {
              "values": [
                {"datetime": "2024-03-15", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00"}
              ]
            }
            """;

        var result = TwelveDataMapper.Map(json, AssetId);
        result.Should().HaveCount(1);
    }
}
