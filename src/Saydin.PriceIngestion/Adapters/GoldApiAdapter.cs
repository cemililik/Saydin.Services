using System.Text.Json;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

public sealed class GoldApiAdapter(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GoldApiAdapter> logger) : IExternalPriceAdapter
{
    public string Source => "goldapi";

    public async Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        Guid assetId, string assetSymbol, string sourceId,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var apiKey = configuration["ExternalApis:GoldApi:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("GoldAPI API key yapılandırılmamış, {Symbol} atlandı", assetSymbol);
            return [];
        }

        var client = httpClientFactory.CreateClient("goldapi");
        var results = new List<PricePoint>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            var point = await FetchSingleDayAsync(client, assetId, sourceId, date, ct);
            if (point is not null)
                results.Add(point);

            // GoldAPI rate limit: freemium'da dakikada ~5 istek — kısa bekleme
            await Task.Delay(250, ct);
        }

        logger.LogInformation(
            "GoldAPI {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
            assetSymbol, results.Count, from, to);

        return results.AsReadOnly();
    }

    private async Task<PricePoint?> FetchSingleDayAsync(
        HttpClient client, Guid assetId, string metal, DateOnly date, CancellationToken ct)
    {
        // metal = "XAU" veya "XAG", currency = "TRY"
        var url = $"{metal}/TRY/{date:yyyyMMdd}";

        try
        {
            using var response = await client.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return GoldApiMapper.Map(json, assetId, date);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GoldAPI veri alınamadı: {Metal} {Date}", metal, date);
            return null;
        }
    }
}
