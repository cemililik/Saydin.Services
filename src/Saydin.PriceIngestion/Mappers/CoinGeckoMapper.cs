using System.Text.Json;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

public static class CoinGeckoMapper
{
    public static IReadOnlyList<PricePoint> Map(string json, Guid assetId, DateOnly from, DateOnly to)
    {
        using var doc = JsonDocument.Parse(json);
        var prices = doc.RootElement.GetProperty("prices");

        // timestamp_ms → günlük kapanış (son değer alınır)
        var daily = new Dictionary<DateOnly, decimal>();

        foreach (var pair in prices.EnumerateArray())
        {
            var timestampMs = pair[0].GetInt64();
            var price       = pair[1].GetDecimal();
            var date        = DateOnly.FromDateTime(
                DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime);

            if (date >= from && date <= to)
                daily[date] = price; // son değeri tut (günlük kapanış)
        }

        return daily
            .Select(kv => new PricePoint
            {
                AssetId   = assetId,
                PriceDate = kv.Key,
                Close     = Math.Round(kv.Value, 6, MidpointRounding.AwayFromZero)
            })
            .OrderBy(p => p.PriceDate)
            .ToList()
            .AsReadOnly();
    }
}
