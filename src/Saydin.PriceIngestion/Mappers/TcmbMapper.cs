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
    /// </summary>
    /// <param name="xml">TCMB'nin döndürdüğü ham XML string.</param>
    /// <param name="assetId">Veritabanındaki asset UUID'si.</param>
    /// <param name="currencyCode">ISO 4217 kodu (örn: "USD", "EUR").</param>
    /// <param name="date">Fiyatın ait olduğu tarih.</param>
    /// <returns>PricePoint veya null (kur bulunamadıysa).</returns>
    public static PricePoint? Map(string xml, Guid assetId, string currencyCode, DateOnly date)
    {
        var doc = XDocument.Parse(xml);

        var currency = doc.Descendants("Currency")
            .FirstOrDefault(c => c.Attribute("CurrencyCode")?.Value == currencyCode);

        if (currency is null) return null;

        // TCMB: ForexSelling = döviz satış kuru (bankacılık/kurumsal referans fiyat)
        // Close = ForexSelling (kanonik fiyat olarak kullanılır)
        // Open  = ForexBuying  (spread'in alt sınırı)
        var forexBuying  = ParseDecimal(currency.Element("ForexBuying")?.Value);
        var forexSelling = ParseDecimal(currency.Element("ForexSelling")?.Value);

        // ForexSelling yoksa (tatil XML'i bozuksa) atla
        if (forexSelling is null) return null;

        return new PricePoint
        {
            AssetId   = assetId,
            PriceDate = date,
            Close     = forexSelling.Value,
            Open      = forexBuying,
        };
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}
