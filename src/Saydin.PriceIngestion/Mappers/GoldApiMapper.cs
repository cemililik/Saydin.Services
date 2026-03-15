using System.Globalization;
using System.Text.Json;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

public static class GoldApiMapper
{
    private const decimal TroyOunceToGram = 31.1034768m;

    public static PricePoint? Map(string json, Guid assetId, DateOnly date)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // price_gram_24k: gram başına TRY fiyatı (close için kullanılır)
        if (!root.TryGetProperty("price_gram_24k", out var gramPriceEl))
            return null;

        var closeGram = gramPriceEl.GetDecimal();
        if (closeGram <= 0) return null;

        // OHLC troy oz → gram dönüşümü
        decimal? OpenGram()  => GetDecimal(root, "open_price")  is { } v ? Math.Round(v / TroyOunceToGram, 6) : null;
        decimal? HighGram()  => GetDecimal(root, "high_price")  is { } v ? Math.Round(v / TroyOunceToGram, 6) : null;
        decimal? LowGram()   => GetDecimal(root, "low_price")   is { } v ? Math.Round(v / TroyOunceToGram, 6) : null;

        return new PricePoint
        {
            AssetId   = assetId,
            PriceDate = date,
            Close     = Math.Round(closeGram, 6, MidpointRounding.AwayFromZero),
            Open      = OpenGram(),
            High      = HighGram(),
            Low       = LowGram()
        };
    }

    private static decimal? GetDecimal(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Null) return null;
        return prop.TryGetDecimal(out var v) ? v : null;
    }
}
