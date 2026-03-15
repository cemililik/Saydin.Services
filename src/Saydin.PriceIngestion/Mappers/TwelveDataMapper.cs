using System.Globalization;
using System.Text.Json;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

public static class TwelveDataMapper
{
    public static IReadOnlyList<PricePoint> Map(string json, Guid assetId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // status != "ok" ise veri yok veya hata
        if (root.TryGetProperty("status", out var statusEl) &&
            statusEl.GetString() != "ok")
            return [];

        if (!root.TryGetProperty("values", out var values))
            return [];

        var results = new List<PricePoint>();

        foreach (var item in values.EnumerateArray())
        {
            if (!DateOnly.TryParse(item.GetProperty("datetime").GetString(), out var date))
                continue;

            if (!decimal.TryParse(item.GetProperty("close").GetString(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                continue;

            results.Add(new PricePoint
            {
                AssetId   = assetId,
                PriceDate = date,
                Close     = close,
                Open      = ParseDecimal(item, "open"),
                High      = ParseDecimal(item, "high"),
                Low       = ParseDecimal(item, "low"),
                Volume    = ParseDecimal(item, "volume")
            });
        }

        return results.OrderBy(p => p.PriceDate).ToList().AsReadOnly();
    }

    private static decimal? ParseDecimal(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var prop)) return null;
        var s = prop.GetString();
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
