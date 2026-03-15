using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

/// <summary>
/// TCMB (Türkiye Cumhuriyet Merkez Bankası) döviz kuru adaptörü.
/// API key gerektirmez. Hafta içi 16:00'dan itibaren güncel kurlar yayınlanır.
/// Endpoint: https://www.tcmb.gov.tr/kurlar/{YYYYMM}/{DDMMYYYY}.xml
/// </summary>
public sealed class TcmbAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<TcmbAdapter> logger) : IExternalPriceAdapter
{
    public string Source => "tcmb";

    public async Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        Guid assetId,
        string assetSymbol,
        string sourceId,       // ISO 4217 kodu: "USD", "EUR"
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("tcmb");
        var results = new List<PricePoint>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            // TCMB hafta sonu yayın yapmaz
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            var point = await FetchSingleDayAsync(client, assetId, sourceId, date, ct);
            if (point is not null)
                results.Add(point);
        }

        logger.LogInformation(
            "TCMB {CurrencyCode}: {Count} fiyat noktası alındı ({From}–{To})",
            sourceId, results.Count, from, to);

        return results.AsReadOnly();
    }

    private async Task<PricePoint?> FetchSingleDayAsync(
        HttpClient client,
        Guid assetId,
        string currencyCode,
        DateOnly date,
        CancellationToken ct)
    {
        // TCMB series kodu "TP.DK.USD.A" → XML'deki CurrencyCode "USD"
        var xmlCurrencyCode = currencyCode.Contains('.')
            ? currencyCode.Split('.')[2]
            : currencyCode;

        // URL formatı: YYYYMM/DDMMYYYY.xml (base address ile birleşir)
        var url = $"{date:yyyyMM}/{date:ddMMyyyy}.xml";

        try
        {
            var response = await client.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Resmi tatil günü — TCMB o gün için dosya yayınlamaz; normal durum
                logger.LogDebug("TCMB resmi tatil veya veri yok: {Date}", date);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(ct);
            return TcmbMapper.Map(xml, assetId, xmlCurrencyCode, date);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tek günlük hata tüm aralığı iptal etmez; logla ve devam et
            logger.LogWarning(ex, "TCMB XML alınamadı: {Date} {CurrencyCode}", date, currencyCode);
            return null;
        }
    }
}
