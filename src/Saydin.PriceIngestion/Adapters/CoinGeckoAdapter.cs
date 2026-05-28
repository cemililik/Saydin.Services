using System.Text.Json;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Adapters;

public sealed class CoinGeckoAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<CoinGeckoAdapter> logger) : IExternalPriceAdapter
{
    public string Source => "coingecko";

    public async Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        Guid assetId, string assetSymbol, string sourceId,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("coingecko");

        var fromUnix = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var toUnix   = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue),   TimeSpan.Zero).ToUnixTimeSeconds();

        var url = $"coins/{Uri.EscapeDataString(sourceId)}/market_chart/range?vs_currency=try&from={fromUnix}&to={toUnix}&precision=6";

        try
        {
            using var response = await client.GetAsync(url, ct);

            // F1.1-4: 429 ve 403'ü sessizce yutmak yerine ExternalApiException ile
            // yukarı bildir. Polly StandardResilienceHandler 429 için zaten retry yapar;
            // bu noktaya geldiyse retry zinciri tükenmiştir (kalıcı hata gibi davran).
            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                                    or System.Net.HttpStatusCode.Forbidden)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds
                              ?? response.Headers.RetryAfter?.Date?.Subtract(DateTimeOffset.UtcNow).TotalSeconds;
                logger.LogWarning(
                    "CoinGecko {StatusCode} ({Symbol}) Retry-After={RetryAfter}s — Polly retry tükendi.",
                    (int)response.StatusCode, assetSymbol, retryAfter);
                throw new ExternalApiException(Source,
                    $"Rate limit / forbidden ({(int)response.StatusCode}) symbol={assetSymbol}");
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var points = CoinGeckoMapper.Map(json, assetId, from, to);

            logger.LogInformation(
                "CoinGecko {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
                assetSymbol, points.Count, from, to);

            return points;
        }
        catch (JsonException ex)
        {
            // F1.1-3 pattern: bozuk payload'u "veri yok" olarak yumuşat — diğer
            // hatalar (HTTP, timeout) Polly sonrası dış katmana fırlar.
            logger.LogWarning(ex, "CoinGecko JSON çözümlenemedi: {Symbol} ({From}–{To})",
                assetSymbol, from, to);
            return [];
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalApiException(Source,
                $"CoinGecko veri alınamadı: {assetSymbol} ({from:yyyy-MM-dd}–{to:yyyy-MM-dd})", ex);
        }
    }
}
