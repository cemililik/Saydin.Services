using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

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
    ILogger<EvdsInflationAdapter> logger) : IInflationAdapter
{
    private const string SeriesCode = "TP.FG.J0";

    public string Source => "evds";

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

        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);
                var outcome = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "auth",
                    System.Net.HttpStatusCode.TooManyRequests => "rate_limit",
                    var s when (int)s >= 500 => "http_5xx",
                    _ => "http_4xx",
                };
                logger.LogError(
                    "EVDS TÜFE API hatası {StatusCode} ({Outcome}): {Body} ({From}–{To})",
                    statusCode, outcome, body, from, to);
                SaydinMetrics.InflationIngestionFailures.Add(1,
                    new KeyValuePair<string, object?>("source", Source),
                    new KeyValuePair<string, object?>("outcome", outcome));
                // F1.1-7: 5xx + 4xx tek noktada — sessizce return [] YASAK; worker
                // ingestion_jobs failed yazsın diye ExternalApiException ile fırlat.
                throw new ExternalApiException(Source,
                    $"EVDS HTTP hatası {statusCode} ({from:yyyy-MM-dd}–{to:yyyy-MM-dd})");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var rates = EvdsInflationMapper.Map(json);

            logger.LogInformation(
                "EVDS TÜFE: {Count} aylık endeks alındı ({From}–{To})",
                rates.Count, from, to);

            return rates;
        }
        catch (HttpRequestException ex)
        {
            // Polly retry tükenmiş network hatası.
            // PR #11 follow-up: metric'in yanı sıra exception stack'i de logla;
            // ExternalApiException sarmalaması inner stack'i sızdırmaz.
            logger.LogError(ex,
                "EVDS network hatası: {Source} ({From}–{To})", Source, from, to);
            SaydinMetrics.InflationIngestionFailures.Add(1,
                new KeyValuePair<string, object?>("source", Source),
                new KeyValuePair<string, object?>("outcome", "transient"));
            throw new ExternalApiException(Source,
                $"EVDS network hatası ({from:yyyy-MM-dd}–{to:yyyy-MM-dd})", ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "EVDS yanıtı çözümlenemedi: {Source} ({From}–{To})", Source, from, to);
            SaydinMetrics.InflationIngestionFailures.Add(1,
                new KeyValuePair<string, object?>("source", Source),
                new KeyValuePair<string, object?>("outcome", "parse"));
            throw new ExternalApiException(Source,
                $"EVDS yanıtı çözümlenemedi ({from:yyyy-MM-dd}–{to:yyyy-MM-dd})", ex);
        }
        finally
        {
            response?.Dispose();
        }
    }
}
