using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Saydin.PriceIngestion.Adapters;
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
        var bytes = Encoding.UTF8.GetBytes(xml);
        var doc = XDocument.Parse(xml);
        return MapInternal(doc, assetId, currencyCode, date,
            SHA256.HashData(bytes), bytes.Length);
    }

    /// <summary>
    /// Önceden parse edilmiş XDocument üzerinden tek sembol için PricePoint üretir.
    /// Adapter cache aynı günün XDocument'ini tek sefer parse edip N sembol için
    /// yeniden kullanır (review F1.1-2: 20 yıl × 30 sembol senaryosunda
    /// XDocument.Parse ~150k → ~5200).
    /// </summary>
    public static PricePoint? Map(
        XDocument doc,
        Guid assetId,
        string currencyCode,
        DateOnly date,
        byte[]? payloadSha256 = null,
        int? payloadByteLength = null) =>
        MapInternal(doc, assetId, currencyCode, date,
            payloadSha256 ?? SHA256.HashData(
                Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting))),
            payloadByteLength ?? Encoding.UTF8.GetByteCount(
                doc.ToString(SaveOptions.DisableFormatting)));

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
        var bytes = Encoding.UTF8.GetBytes(xml);
        var hash = SHA256.HashData(bytes);
        var results = new List<PricePoint>(currencyCodeToAssetId.Count);

        foreach (var (currencyCode, assetId) in currencyCodeToAssetId)
        {
            var point = MapInternal(doc, assetId, currencyCode, date, hash, bytes.Length);
            if (point is not null)
                results.Add(point);
        }

        return results;
    }

    private static PricePoint? MapInternal(
        XDocument doc,
        Guid assetId,
        string currencyCode,
        DateOnly date,
        byte[] payloadSha256,
        int payloadByteLength)
    {
        ValidatePayloadDate(doc, date);
        var currency = doc.Descendants("Currency")
            .FirstOrDefault(c => c.Attribute("CurrencyCode")?.Value == currencyCode);

        if (currency is null) return null;

        // Unit: 1 (USD/EUR/...) veya 100 (JPY/KRW/IDR/...). XML'de yoksa 1 varsayılır.
        var unitText = currency.Element("Unit")?.Value;
        var unit = string.IsNullOrWhiteSpace(unitText) ? 1m : ParseDecimal(unitText);
        if (unit is null || unit <= 0m)
            throw new ProviderContractException("contract_unit_invalid");

        var forexBuying = ParseDecimal(currency.Element("ForexBuying")?.Value);
        if (forexBuying is null) return null;
        if (forexBuying <= 0m)
            throw new ProviderContractException("contract_price_invalid");

        var close = Normalize(forexBuying.Value, unit.Value);
        var observationId = $"tcmb:{currencyCode}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:forex_buying";
        var asOf = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var evidence = ObservationEvidence.Create(
            ("as_of_at", asOf),
            ("close", close),
            ("currency", currencyCode),
            ("date", date),
            ("observation_id", observationId),
            ("provider_source", ProviderSources.Tcmb),
            ("unit", unit.Value));
        return ProviderAuthority.Price(new PricePoint
        {
            AssetId   = assetId,
            PriceDate = date,
            Close     = close,
            // Open / High / Low = null (TCMB intra-day OHLC yayınlamaz, sadece bid kuru).
        }, ProviderSources.Tcmb, observationId, asOf,
            ObservationPriceKinds.OfficialReference, payloadSha256, payloadByteLength, evidence);
    }

    private static decimal Normalize(decimal value, decimal unit) =>
        Math.Round(value / unit, 6, MidpointRounding.AwayFromZero);

    private static void ValidatePayloadDate(XDocument document, DateOnly requestedDate)
    {
        var root = document.Root;
        if (root?.Name.LocalName != "Tarih_Date")
            throw new ProviderContractException("contract_root_mismatch");
        var observed = new List<DateOnly>(2);
        var date = root.Attribute("Date")?.Value;
        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!DateOnly.TryParseExact(date, "MM/dd/yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                throw new ProviderContractException("contract_observation_date_invalid");
            observed.Add(parsed);
        }
        var tarih = root.Attribute("Tarih")?.Value;
        if (!string.IsNullOrWhiteSpace(tarih))
        {
            if (!DateOnly.TryParseExact(tarih, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                throw new ProviderContractException("contract_observation_date_invalid");
            observed.Add(parsed);
        }
        if (observed.Count == 0 || observed.Any(value => value != requestedDate))
            throw new ProviderContractException("contract_observation_date_mismatch");
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}
