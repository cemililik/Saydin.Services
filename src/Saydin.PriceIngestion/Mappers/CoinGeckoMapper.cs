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

        // Her gün için "midnight'a uzaklık (ms) + ham timestamp + fiyat" tutulur.
        // INGR-003: tie-breaking deterministik — aynı uzaklıkta iki gözlem geldiğinde
        // **küçük timestamp** (yani önce gelen, daha "midnight'a doğru" olan) kazanır.
        // Önceki strict `<` aynı timestamp'in iki kez geldiği patolojik girişte array
        // sırasına bağlıydı; deterministik tie-break sırayı dışarıdan tahmin edilebilir kılar.
        var daily = new Dictionary<DateOnly, (long DistanceMs, long TimestampMs, decimal Price)>();

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

            if (!daily.TryGetValue(date, out var existing)
                || distanceMs < existing.DistanceMs
                || (distanceMs == existing.DistanceMs && timestampMs < existing.TimestampMs))
            {
                daily[date] = (distanceMs, timestampMs, price);
            }
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

