using FluentAssertions;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Constants;

namespace Saydin.PriceIngestion.Tests.Adapters;

/// <summary>
/// EVDS TÜFE JSON yanıtının InflationRate listesine doğru dönüştürüldüğünü doğrular.
/// Gerçek EVDS yanıt formatı: { "items": [{ "Tarih": "2025-1", "TP_FG_J0": "2819.65", "UNIXTIME": ... }] }
/// </summary>
public class EvdsInflationMapperTests
{
    // ── Geçerli JSON örnekleri ───────────────────────────────────────────────

    private const string ValidJson = """
        {
          "items": [
            { "Tarih": "2020-1",  "TP_FG_J0": "532.38", "UNIXTIME": { "unixtime": 1577836800 } },
            { "Tarih": "2020-2",  "TP_FG_J0": "537.10", "UNIXTIME": { "unixtime": 1580515200 } },
            { "Tarih": "2020-12", "TP_FG_J0": "598.00", "UNIXTIME": { "unixtime": 1606780800 } }
          ]
        }
        """;

    [Fact]
    public void Map_ValidJson_ReturnsCorrectCount()
    {
        var result = EvdsInflationMapper.Map(ValidJson);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void Map_ValidJson_ParsesIndexValueCorrectly()
    {
        var result = EvdsInflationMapper.Map(ValidJson);

        result[0].IndexValue.Should().Be(532.38m);
        result[1].IndexValue.Should().Be(537.10m);
        result[2].IndexValue.Should().Be(598.00m);
    }

    [Fact]
    public void Map_DateFormat_YYYY_M_ParsedAsFirstOfMonth()
    {
        // "2020-1" → 2020-01-01
        var result = EvdsInflationMapper.Map(ValidJson);

        result[0].PeriodDate.Should().Be(new DateOnly(2020, 1, 1));
        result[1].PeriodDate.Should().Be(new DateOnly(2020, 2, 1));
    }

    [Fact]
    public void Map_DateFormat_YYYY_MM_TwoDigitMonth_ParsedCorrectly()
    {
        // "2020-12" → 2020-12-01
        var result = EvdsInflationMapper.Map(ValidJson);

        result[2].PeriodDate.Should().Be(new DateOnly(2020, 12, 1));
    }

    [Fact]
    public void Map_DefaultSource_IsTuik()
    {
        // Default source = InflationSources.Tuik (sabit; hardcoded literal değil).
        var result = EvdsInflationMapper.Map(ValidJson);

        result.Should().AllSatisfy(r => r.Source.Should().Be(InflationSources.Tuik));
    }

    [Fact]
    public void Map_PersistedSource_IsAlwaysAValidCheckConstraintValue()
    {
        // INGR-010/INGR-011 regresyon koruması: mapper'ın persist ettiği source DEĞERİ
        // chk_inflation_rates_source / composite-PK kümesinde (InflationSources.Lookup)
        // OLMALI. Üretim yolu EvdsInflationAdapter `InflationSources.Tuik` geçirir; eğer
        // birisi yanlışlıkla "evds" gibi kanal kimliği geçirirse bu test kırmızıya döner.
        var result = EvdsInflationMapper.Map(ValidJson, source: InflationSources.Tuik);

        result.Should().NotBeEmpty();
        result.Should().AllSatisfy(r =>
            InflationSources.Lookup.Should().Contain(r.Source));
    }

    // ── ND (No Data) değerleri atlanmalı ────────────────────────────────────

    [Fact]
    public void Map_NdValue_SkipsRow()
    {
        const string json = """
            {
              "items": [
                { "Tarih": "2003-1", "TP_FG_J0": "ND",    "UNIXTIME": {} },
                { "Tarih": "2003-2", "TP_FG_J0": "100.0", "UNIXTIME": {} }
              ]
            }
            """;

        var result = EvdsInflationMapper.Map(json);

        result.Should().HaveCount(1);
        result[0].PeriodDate.Should().Be(new DateOnly(2003, 2, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\\t")]   // JSON gövdesinde escape edilmiş tab — IsNullOrWhiteSpace true verir
    public void Map_EmptyOrWhitespaceValue_SkipsRow(string emptyValue)
    {
        var json = $$"""
            {
              "items": [
                { "Tarih": "2020-1", "TP_FG_J0": "{{emptyValue}}", "UNIXTIME": {} },
                { "Tarih": "2020-2", "TP_FG_J0": "537.10", "UNIXTIME": {} }
              ]
            }
            """;

        var result = EvdsInflationMapper.Map(json);

        result.Should().HaveCount(1);
    }

    // ── Eksik / bozuk alanlar ────────────────────────────────────────────────

    [Fact]
    public void Map_MissingItemsKey_ReturnsEmpty()
    {
        const string json = """{ "data": [] }""";

        var result = EvdsInflationMapper.Map(json);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Map_MissingTarihField_SkipsRow()
    {
        const string json = """
            {
              "items": [
                { "TP_FG_J0": "532.38", "UNIXTIME": {} }
              ]
            }
            """;

        var result = EvdsInflationMapper.Map(json);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Map_InvalidDateFormat_SkipsRow()
    {
        const string json = """
            {
              "items": [
                { "Tarih": "2020-99", "TP_FG_J0": "532.38", "UNIXTIME": {} },
                { "Tarih": "abcd",    "TP_FG_J0": "537.10", "UNIXTIME": {} },
                { "Tarih": "2020-3",  "TP_FG_J0": "540.00", "UNIXTIME": {} }
              ]
            }
            """;

        var result = EvdsInflationMapper.Map(json);

        result.Should().HaveCount(1);
        result[0].PeriodDate.Should().Be(new DateOnly(2020, 3, 1));
    }

    [Fact]
    public void Map_EmptyItemsArray_ReturnsEmpty()
    {
        const string json = """{ "items": [] }""";

        var result = EvdsInflationMapper.Map(json);

        result.Should().BeEmpty();
    }

    // ── Ondalık sayı formatı ─────────────────────────────────────────────────

    [Fact]
    public void Map_DecimalWithDot_ParsedCorrectly()
    {
        const string json = """
            {
              "items": [
                { "Tarih": "2025-1", "TP_FG_J0": "2819.65", "UNIXTIME": {} }
              ]
            }
            """;

        var result = EvdsInflationMapper.Map(json);

        result[0].IndexValue.Should().Be(2819.65m);
    }

    [Fact]
    public void Map_NonPositiveIndex_IsPermanentContractFailure()
    {
        const string json = """{"items":[{"Tarih":"2025-1","TP_FG_J0":"0"}]}""";
        var act = () => EvdsInflationMapper.Map(json);
        act.Should().Throw<Saydin.PriceIngestion.Adapters.ProviderContractException>()
            .Which.Code.Should().Be("contract_index_value_invalid");
    }
}
