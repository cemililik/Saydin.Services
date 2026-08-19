using FluentAssertions;
using Saydin.PriceIngestion.Adapters;
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
            "exchange": "BIST",
            "mic_code": "XIST",
            "exchange_timezone": "Europe/Istanbul",
            "type": "Common Stock"
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
        var result = TwelveDataMapper.Map(ValidJson, AssetId, "AKBNK:BIST");

        result.Should().HaveCount(2);
        result[0].PriceDate.Should().Be(new DateOnly(2024, 3, 14)); // eski → yeni sıralama
        result[1].PriceDate.Should().Be(new DateOnly(2024, 3, 15));
    }

    [Fact]
    public void Map_ValidJson_OhlcvParsedCorrectly()
    {
        var result = TwelveDataMapper.Map(ValidJson, AssetId, "AKBNK:BIST");
        var latest = result[1]; // 2024-03-15

        latest.AssetId.Should().Be(AssetId);
        latest.Close.Should().Be(48.10m);
        latest.Open.Should().Be(47.12m);
        latest.High.Should().Be(48.50m);
        latest.Low.Should().Be(46.90m);
        latest.Volume.Should().Be(15234567m);
    }

    [Theory]
    [InlineData("\"symbol\": \"AKBNK\"", "\"symbol\": \"THYAO\"")]
    [InlineData("\"interval\": \"1day\"", "\"interval\": \"1h\"")]
    [InlineData("\"mic_code\": \"XIST\"", "\"mic_code\": \"XNYS\"")]
    [InlineData("\"exchange_timezone\": \"Europe/Istanbul\"", "\"exchange_timezone\": \"UTC\"")]
    public void Map_ResponseIdentityMismatch_IsPermanentContractFailure(
        string expected, string replacement)
    {
        var act = () => TwelveDataMapper.Map(
            ValidJson.Replace(expected, replacement, StringComparison.Ordinal),
            AssetId, "AKBNK:BIST");
        act.Should().Throw<ProviderContractException>()
            .Which.Code.Should().Be("contract_identity_mismatch");
    }

    // ── status != "ok" ────────────────────────────────────────────────────────

    [Fact]
    public void Map_StatusError_ThrowsStableProviderContractException()
    {
        // F1.1-6 ([G-D-03]): status="error" sessizce empty list dönmek yerine
        // ExternalApiException fırlatır — caller ingestion_jobs failed kaydı oluşturur.
        const string json = """
            {
              "status": "error",
              "code": 404,
              "message": "**symbol** not found: YOKHISSE"
            }
            """;

        var act = () => TwelveDataMapper.Map(json, AssetId, "YOKHISSE", "twelvedata");
        act.Should().Throw<ProviderContractException>()
           .Which.Code.Should().Be("provider_error");
    }

    [Fact]
    public void Map_UnknownStatus_ThrowsStableProviderContractException()
    {
        const string json = """{"status": "rate_limited"}""";
        var act = () => TwelveDataMapper.Map(json, AssetId, "AKBNK", "twelvedata");
        act.Should().Throw<ProviderContractException>()
           .Which.Code.Should().Be("provider_status_invalid");
    }

    // ── Eksik values ──────────────────────────────────────────────────────────

    [Fact]
    public void Map_MissingValuesProperty_ReturnsEmptyList()
    {
        const string json = """{"status": "ok"}""";
        var result = TwelveDataMapper.MapContractlessFixture(json, AssetId);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Map_EmptyValuesArray_ReturnsEmptyList()
    {
        const string json = """{"status": "ok", "values": []}""";
        var result = TwelveDataMapper.MapContractlessFixture(json, AssetId);
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
                {"datetime": "BOZUK", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00", "volume": "1"},
                {"datetime": "2024-03-15", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00", "volume": "1"}
              ]
            }
            """;

        var result = TwelveDataMapper.MapContractlessFixture(json, AssetId);
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
                {"datetime": "2024-03-14", "close": "N/A", "open": "47.00", "high": "49.00", "low": "46.00", "volume": "1"},
                {"datetime": "2024-03-15", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00", "volume": "1"}
              ]
            }
            """;

        var result = TwelveDataMapper.MapContractlessFixture(json, AssetId);
        result.Should().HaveCount(1);
        result[0].PriceDate.Should().Be(new DateOnly(2024, 3, 15));
    }

    // ── Opsiyonel alanlar ────────────────────────────────────────────────────

    [Fact]
    public void Map_MissingVolume_RejectsIncompleteDailyBar()
    {
        const string json = """
            {
              "status": "ok",
              "values": [
                {"datetime": "2024-03-15", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00"}
              ]
            }
            """;

        var result = TwelveDataMapper.MapContractlessFixture(json, AssetId);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Map_MissingOpenHighLow_RejectsNonOhlcDailyClose()
    {
        const string json = """
            {
              "status": "ok",
              "values": [
                {"datetime": "2024-03-15", "close": "48.10"}
              ]
            }
            """;

        var result = TwelveDataMapper.MapContractlessFixture(json, AssetId);
        result.Should().BeEmpty();
    }

    // ── Status yok ama values var (toleranslı) ───────────────────────────────

    [Fact]
    public void Map_NoStatusField_ParsesSuccessfully()
    {
        const string json = """
            {
              "values": [
                {"datetime": "2024-03-15", "close": "48.10", "open": "47.00", "high": "49.00", "low": "46.00", "volume": "1"}
              ]
            }
            """;

        var result = TwelveDataMapper.MapContractlessFixture(json, AssetId);
        result.Should().HaveCount(1);
    }
}
