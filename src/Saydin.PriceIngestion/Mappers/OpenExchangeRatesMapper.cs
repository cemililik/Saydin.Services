using System.Text.Json;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

public static class OpenExchangeRatesMapper
{
    // 1 troy oz = 31.1034768 gram
    private const decimal TroyOunceToGram = 31.1034768m;

    /// <summary>
    /// OXR yanıtından belirtilen metale ait gram/TRY fiyatını hesaplar.
    /// OXR her zaman USD base döner; çapraz kur hesabı yapılır.
    /// </summary>
    /// <param name="json">OXR historical endpoint yanıtı</param>
    /// <param name="assetId">Veritabanı asset UUID'si</param>
    /// <param name="date">Fiyat tarihi</param>
    /// <param name="metalCode">Sembol: "XAU" (altın) veya "XAG" (gümüş)</param>
    public static PricePoint? Map(string json, Guid assetId, DateOnly date, string metalCode)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("rates", out var rates))
            return null;

        // rates["XAU"] = troy oz başına USD'nin karşılığı (1 USD = X troy oz)
        // → 1 troy oz fiyatı USD = 1 / rates["XAU"]
        if (!rates.TryGetProperty(metalCode, out var metalRateEl) ||
            !metalRateEl.TryGetDecimal(out var metalRate) || metalRate <= 0)
            return null;

        // rates["TRY"] = 1 USD kaç TRY
        if (!rates.TryGetProperty("TRY", out var tryRateEl) ||
            !tryRateEl.TryGetDecimal(out var tryRate) || tryRate <= 0)
            return null;

        var priceUsdPerOz  = 1m / metalRate;
        var priceTryPerOz  = priceUsdPerOz * tryRate;
        var priceTryPerGram = Math.Round(priceTryPerOz / TroyOunceToGram, 6, MidpointRounding.AwayFromZero);

        if (priceTryPerGram <= 0) return null;

        return new PricePoint
        {
            AssetId   = assetId,
            PriceDate = date,
            Close     = priceTryPerGram
            // OXR historical endpoint OHLC sağlamaz, sadece close değeri var
        };
    }
}
