using System.Text.Json;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;

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

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning(
                    "CoinGecko {StatusCode}: {Symbol} atlandı. API key gerekiyor olabilir (ExternalApis:CoinGecko:ApiKey).",
                    (int)response.StatusCode, assetSymbol);
                return [];
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var points = CoinGeckoMapper.Map(json, assetId, from, to);

            logger.LogInformation(
                "CoinGecko {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
                assetSymbol, points.Count, from, to);

            return points;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CoinGecko veri alınamadı: {Symbol} ({From}–{To})", assetSymbol, from, to);
            return [];
        }
    }
}
