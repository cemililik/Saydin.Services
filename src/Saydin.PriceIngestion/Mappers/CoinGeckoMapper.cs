using System.Text.Json;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

public static class CoinGeckoMapper
{
    /// <summary>
    /// CoinGecko market_chart endpoint'i UTC midnight değerlerine ek olarak
    /// gün-içi snapshot'lar döndürebilir (örn. en son fiyat 23:55). Önceki sürüm
    /// "son değeri tut" mantığıyla 23:55'i kapanış olarak yazıyordu — bu intra-day
    /// noise yaratıyordu. F2.4-6 ([C-D-25]): yalnızca <c>00:00 UTC</c>'ye en yakın
    /// gözlem alınır; aynı tarih için birden fazla gözlem varsa midnight'a en
    /// yakın olan kazanır (timestamp_ms tabanlı).
    /// </summary>
    public static IReadOnlyList<PricePoint> Map(string json, Guid assetId, DateOnly from, DateOnly to)
    {
        using var doc = JsonDocument.Parse(json);
        var prices = doc.RootElement.GetProperty("prices");

        // Her gün için "midnight'a uzaklık (ms) + fiyat" tutulur. İlk gözlem koşulsuz
        // yazılır; sonraki gözlem midnight'a daha yakınsa üzerine yazılır.
        var daily = new Dictionary<DateOnly, (long DistanceMs, decimal Price)>();

        foreach (var pair in prices.EnumerateArray())
        {
            var timestampMs = pair[0].GetInt64();
            var price       = pair[1].GetDecimal();
            var utcMoment   = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
            var date        = DateOnly.FromDateTime(utcMoment.UtcDateTime);
            if (date < from || date > to) continue;

            // Bu günün midnight UTC referans noktası.
            var midnight    = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
            var distanceMs  = Math.Abs((long)(utcMoment - midnight).TotalMilliseconds);

            if (!daily.TryGetValue(date, out var existing) || distanceMs < existing.DistanceMs)
                daily[date] = (distanceMs, price);
        }

        return daily
            .Select(kv => new PricePoint
            {
                AssetId   = assetId,
                PriceDate = kv.Key,
                Close     = Math.Round(kv.Value.Price, 6, MidpointRounding.AwayFromZero)
            })
            .OrderBy(p => p.PriceDate)
            .ToList()
            .AsReadOnly();
    }
}
