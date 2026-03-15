using System.Text.Json;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;

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

        var url = $"time_series?symbol={Uri.EscapeDataString(sourceId)}" +
                  $"&interval=1day&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}" +
                  $"&outputsize=5000&format=JSON&apikey={apiKey}";

        try
        {
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var points = TwelveDataMapper.Map(json, assetId);

            logger.LogInformation(
                "TwelveData {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
                assetSymbol, points.Count, from, to);

            return points;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TwelveData veri alınamadı: {Symbol} ({From}–{To})", assetSymbol, from, to);
            return [];
        }
    }
}
