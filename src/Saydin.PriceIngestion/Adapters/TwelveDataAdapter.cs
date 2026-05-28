using System.Text.Json;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Adapters;

public sealed class TwelveDataAdapter(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<TwelveDataAdapter> logger) : IExternalPriceAdapter
{
    public string Source => "twelvedata";

    public async Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        Guid assetId, string assetSymbol, string sourceId,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var apiKey = configuration["ExternalApis:TwelveData:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("TwelveData API key yapılandırılmamış, {Symbol} atlandı", assetSymbol);
            return [];
        }

        var client = httpClientFactory.CreateClient("twelvedata");

        // F1.1-5: apikey URL query string'inde değil HTTP header'da gönderilir;
        // aksi halde OTel trace span'larında "Url" attribute olarak secret sızar.
        // TwelveData "Authorization: apikey <key>" header şemasını destekler.
        // F2.4-3: outputsize'ı config'e taşı (default 5000 üst limit).
        var outputSize = configuration.GetValue("ExternalApis:TwelveData:OutputSize", 5000);
        var url = $"time_series?symbol={Uri.EscapeDataString(sourceId)}" +
                  $"&interval=1day&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}" +
                  $"&outputsize={outputSize}&format=JSON";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"apikey {apiKey}");

        try
        {
            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning(
                    "TwelveData 429 ({Symbol}) — Polly retry tükendi.", assetSymbol);
                throw new ExternalApiException(Source,
                    $"Rate limit (429) symbol={assetSymbol}");
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var points = TwelveDataMapper.Map(json, assetId, assetSymbol, Source);

            logger.LogInformation(
                "TwelveData {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
                assetSymbol, points.Count, from, to);

            return points;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "TwelveData JSON çözümlenemedi: {Symbol} ({From}–{To})",
                assetSymbol, from, to);
            return [];
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalApiException(Source,
                $"TwelveData veri alınamadı: {assetSymbol} ({from:yyyy-MM-dd}–{to:yyyy-MM-dd})", ex);
        }
    }
}
