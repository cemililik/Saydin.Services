using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

/// <summary>
/// TCMB EVDS (Elektronik Veri Dağıtım Sistemi) TÜFE endeks adaptörü.
/// Seri: TP.FG.J0 — Tüketici Fiyat Endeksi Genel, 2003=100 bazlı.
/// API key gerektirir: evds3.tcmb.gov.tr üzerinden ücretsiz alınır.
/// Endpoint: https://evds3.tcmb.gov.tr/igmevdsms-dis/series=TP.FG.J0&startDate=DD-MM-YYYY&endDate=DD-MM-YYYY&type=json&frequency=5
/// DİKKAT: key, query param değil HTTP Request Header olarak gönderilmelidir.
/// </summary>
public sealed class EvdsInflationAdapter(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<EvdsInflationAdapter> logger)
{
    private const string SeriesCode = "TP.FG.J0";

    public async Task<IReadOnlyList<InflationRate>> FetchRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var apiKey = configuration["ExternalApis:Evds:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("EVDS API key yapılandırılmamış. Enflasyon verisi çekilemiyor.");
            return [];
        }

        var client = httpClientFactory.CreateClient("evds");

        // EVDS tarih formatı: DD-MM-YYYY; key HTTP header olarak gönderilir (query param değil)
        // frequency=5: aylık; formulas=0: düzey (ham endeks değeri)
        var startDate = from.ToString("dd-MM-yyyy");
        var endDate   = to.ToString("dd-MM-yyyy");
        var url = $"igmevdsms-dis/series={SeriesCode}&startDate={startDate}&endDate={endDate}&type=json&frequency=5&formulas=0";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("key", apiKey);

        try
        {
            var response = await client.SendAsync(request, ct);

            // 4xx hataları kalıcıdır — sessizce yutmak yerine yukarı fırlat
            if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogError(
                    "EVDS TÜFE kalıcı API hatası {StatusCode}: {Body} ({From}–{To})",
                    (int)response.StatusCode, body, from, to);
                throw new HttpRequestException(
                    $"EVDS API kalıcı hata: {(int)response.StatusCode}", null, response.StatusCode);
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var rates = EvdsInflationMapper.Map(json);

            logger.LogInformation(
                "EVDS TÜFE: {Count} aylık endeks alındı ({From}–{To})",
                rates.Count, from, to);

            return rates;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // 4xx hataları zaten loglandı, yukarı fırlat (caller'a bildir)
            throw;
        }
        catch (Exception ex)
        {
            // Geçici hatalar (network, deserialization vb.) — boş liste dön
            logger.LogError(ex, "EVDS TÜFE geçici hata ({From}–{To})", from, to);
            return [];
        }
    }
}
