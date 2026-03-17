using System.Globalization;
using System.Text.Json;
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

    public static IReadOnlyList<InflationRate> Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("items", out var items))
            return [];

        var rates = new List<InflationRate>();
        var now   = DateTimeOffset.UtcNow;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("Tarih", out var dateEl))
                continue;

            if (!TryParseDate(dateEl.GetString(), out var periodDate))
                continue;

            if (!item.TryGetProperty(FieldName, out var valueEl))
                continue;

            var valueStr = valueEl.GetString();
            if (string.IsNullOrWhiteSpace(valueStr) || valueStr == "ND")
                continue;

            if (!decimal.TryParse(valueStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var indexValue))
                continue;

            rates.Add(new InflationRate
            {
                PeriodDate = periodDate,
                IndexValue = indexValue,
                Source     = "tuik",
                CreatedAt  = now,
                UpdatedAt  = now,
            });
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
