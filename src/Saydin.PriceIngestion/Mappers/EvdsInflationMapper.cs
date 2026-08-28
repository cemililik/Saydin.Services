using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

/// <summary>
/// TCMB EVDS JSON yanıtını InflationRate entity'lerine dönüştürür.
/// TP.FG.J0 serisi: JSON field adı TP_FG_J0 (EVDS nokta → alt çizgi).
/// Tarih formatı: "2025-1" (YYYY-M) — her ayın 1. günü olarak kaydedilir.
/// "ND" değerleri (veri yok) atlanır.
/// </summary>
public static class EvdsInflationMapper
{
    // TP.FG.J0 → EVDS JSON field: TP_FG_J0
    private const string FieldName = "TP_FG_J0";

    public static IReadOnlyList<InflationRate> Map(
        string json,
        string source = InflationSources.Tuik,
        byte[]? payloadSha256 = null,
        int? payloadByteLength = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ProviderContractException("contract_value_kind_invalid");

        if (!root.TryGetProperty("items", out var items))
            return [];
        if (items.ValueKind != JsonValueKind.Array)
            throw new ProviderContractException("contract_value_kind_invalid");

        var rates = new List<InflationRate>();
        var now   = DateTimeOffset.UtcNow;
        var payloadHash = payloadSha256 ?? SHA256.HashData(Encoding.UTF8.GetBytes(json));

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ProviderContractException("contract_value_kind_invalid");
            if (!item.TryGetProperty("Tarih", out var dateEl))
                continue;

            if (!TryParseDate(ProviderValueParser.ReadString(dateEl), out var periodDate))
                continue;

            if (!item.TryGetProperty(FieldName, out var valueEl))
                continue;

            if (valueEl.ValueKind == JsonValueKind.String)
            {
                var valueText = ProviderValueParser.ReadString(valueEl);
                if (string.IsNullOrWhiteSpace(valueText) || valueText == "ND")
                    continue;
            }

            if (!ProviderValueParser.TryReadDecimal(valueEl, out var indexValue))
                continue;
            if (indexValue <= 0)
                throw new ProviderContractException("contract_index_value_invalid");

            var observationId = $"evds:{FieldName}:{periodDate.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";
            var asOf = new DateTimeOffset(periodDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var evidence = ObservationEvidence.Create(
                ("as_of_at", asOf),
                ("date", periodDate),
                ("index_value", indexValue),
                ("observation_id", observationId),
                ("provider_source", ProviderSources.Evds),
                ("series", "TP.FG.J0"));
            rates.Add(ProviderAuthority.Inflation(new InflationRate
            {
                PeriodDate = periodDate,
                IndexValue = indexValue,
                // INGR-010: source = veri KÖKENİ (data origin). EVDS adaptörü
                // InflationSources.Tuik geçirir (TP.FG.J0 = TÜİK TÜFE serisi). Dış-API
                // kanal kimliği "evds" ise ingestion_jobs.source'ta tutulur (kısıtsız).
                // chk_inflation_rates_source + composite PK yalnız 'tuik'/'seed-approximation'
                // kabul eder; buraya "evds" yazmak CHECK ihlali verir.
                Source     = source,
                CreatedAt  = now,
                UpdatedAt  = now,
            }, ProviderSources.Evds, observationId, asOf, payloadHash,
                payloadByteLength ?? Encoding.UTF8.GetByteCount(json), evidence));
        }

        return rates.AsReadOnly();
    }

    /// <summary>
    /// EVDS tarih formatı: "2025-1" (YYYY-M veya YYYY-MM).
    /// Her kayıt ayın 1. günü olarak döndürülür.
    /// </summary>
    private static bool TryParseDate(string? raw, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Örnek: "2025-1" veya "2025-12"
        var parts = raw.Split('-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var year))  return false;
        if (!int.TryParse(parts[1], out var month)) return false;
        if (year < 2000 || year > 2100) return false;
        if (month < 1 || month > 12)    return false;

        result = new DateOnly(year, month, 1);
        return true;
    }
}
