using System.Globalization;
using System.Xml.Linq;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

/// <summary>
/// TCMB XML yanıtını PricePoint entity'sine dönüştürür.
/// TCMB XML formatı: https://www.tcmb.gov.tr/kurlar/YYYYMM/DDMMYYYY.xml
/// </summary>
public static class TcmbMapper
{
    /// <summary>
    /// Ham TCMB XML metnini parse ederek belirtilen para birimi için PricePoint üretir.
    ///
    /// TCMB bazı para birimlerini 100 birim üzerinden kote eder (JPY, KRW, IDR vb.):
    /// XML'deki &lt;Unit&gt; elementi bu çarpanı verir. Hesaplamalarda kullanıcıya
    /// "1 birim X kaç TL" göstermek için Unit'e bölerek normalize ediyoruz —
    /// böylece JPY/TL gibi düşük-değerli kurlar 100x büyütülmüş olarak saklanmaz.
    ///
    /// F1.4-1 (review C-D-23): TCMB intra-day OHLC yayımlamaz — sadece "ForexBuying"
    /// (alış kuru) ve "ForexSelling" (satış kuru) bilgilerini günlük yayınlar.
    /// Önceki kod Open=ForexBuying, Close=ForexSelling olarak yazıyordu; bu OHLC
    /// semantiğine uymaz (Open "açılış", Close "kapanış" anlamındadır).
    /// Yeni davranış: Close = ForexBuying / Unit (kanonik referans/bid kuru);
    /// Open / High / Low alanları null bırakılır (gerçek değerleri yok).
    /// </summary>
    /// <param name="xml">TCMB'nin döndürdüğü ham XML string.</param>
    /// <param name="assetId">Veritabanındaki asset UUID'si.</param>
    /// <param name="currencyCode">ISO 4217 kodu (örn: "USD", "EUR").</param>
    /// <param name="date">Fiyatın ait olduğu tarih.</param>
    /// <returns>PricePoint veya null (kur bulunamadıysa).</returns>
    public static PricePoint? Map(string xml, Guid assetId, string currencyCode, DateOnly date)
    {
        var doc = XDocument.Parse(xml);
        return MapInternal(doc, assetId, currencyCode, date);
    }

    /// <summary>
    /// XML'i bir kez parse edip aynı doc üzerinden N para birimi için PricePoint üretir
    /// (review F1.1-2: TCMB tek günlük XML tüm sembolleri içerir; gün-bazlı dedup).
    /// </summary>
    public static IReadOnlyList<PricePoint> MapMany(
        string xml,
        IReadOnlyDictionary<string, Guid> currencyCodeToAssetId,
        DateOnly date)
    {
        var doc = XDocument.Parse(xml);
        var results = new List<PricePoint>(currencyCodeToAssetId.Count);

        foreach (var (currencyCode, assetId) in currencyCodeToAssetId)
        {
            var point = MapInternal(doc, assetId, currencyCode, date);
            if (point is not null)
                results.Add(point);
        }

        return results;
    }

    private static PricePoint? MapInternal(XDocument doc, Guid assetId, string currencyCode, DateOnly date)
    {
        var currency = doc.Descendants("Currency")
            .FirstOrDefault(c => c.Attribute("CurrencyCode")?.Value == currencyCode);

        if (currency is null) return null;

        // Unit: 1 (USD/EUR/...) veya 100 (JPY/KRW/IDR/...). XML'de yoksa 1 varsayılır.
        var unit = ParseDecimal(currency.Element("Unit")?.Value) ?? 1m;
        if (unit <= 0m) unit = 1m;

        var forexBuying = ParseDecimal(currency.Element("ForexBuying")?.Value);
        if (forexBuying is null) return null;

        return new PricePoint
        {
            AssetId   = assetId,
            PriceDate = date,
            Close     = Normalize(forexBuying.Value, unit),
            // Open / High / Low = null (TCMB intra-day OHLC yayınlamaz, sadece bid kuru).
        };
    }

    private static decimal Normalize(decimal value, decimal unit) =>
        Math.Round(value / unit, 6, MidpointRounding.AwayFromZero);

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}
