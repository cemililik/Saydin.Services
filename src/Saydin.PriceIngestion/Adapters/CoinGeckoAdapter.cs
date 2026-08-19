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

    public async Task<AdapterOutcome<PricePoint>> FetchRangeAsync(
        PriceFetchRequest request, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("coingecko");

        // Official range docs describe daily points at 00:00 UTC for sufficiently long ranges,
        // while official plan support says interval overrides are unavailable on Demo/Public
        // and most paid plans. We therefore do not send interval=daily or assume Enterprise.
        // A >=91-day range requests provider-selected daily granularity; any hourly result in the
        // caller's target range is rejected by CoinGeckoMapper, never rounded to "nearest".
        // https://docs.coingecko.com/reference/coins-id-market-chart-range
        // https://support.coingecko.com/hc/en-us/articles/4538771776153
        var fetchFrom = request.From;
        if (request.To.DayNumber - request.From.DayNumber < 90)
            fetchFrom = request.To.AddDays(-90);
        var fromUnix = new DateTimeOffset(
            fetchFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var toUnix = new DateTimeOffset(
            request.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

        var url = $"coins/{Uri.EscapeDataString(request.SourceId)}/market_chart/range?vs_currency=try&from={fromUnix}&to={toUnix}&precision=6";

        try
        {
            using var response = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);

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
                    (int)response.StatusCode, request.AssetSymbol, retryAfter);
                return response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? AdapterOutcome<PricePoint>.RetryableFailure("http_429", $"symbol={request.AssetSymbol}")
                    : AdapterOutcome<PricePoint>.PermanentFailure("auth_forbidden", $"symbol={request.AssetSymbol}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                return code >= 500
                    ? AdapterOutcome<PricePoint>.RetryableFailure("http_5xx", $"status={code}")
                    : AdapterOutcome<PricePoint>.PermanentFailure("http_4xx", $"status={code}");
            }

            var payload = await BoundedHttpContent.ReadAsync(response.Content, ct);
            using var document = JsonDocument.Parse(payload.Bytes);
            if (!document.RootElement.TryGetProperty("prices", out var prices)
                || prices.ValueKind != JsonValueKind.Array)
                return AdapterOutcome<PricePoint>.PermanentFailure("schema_missing_prices");

            var rawItemCount = prices.EnumerateArray().Count(pair =>
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2
                    || !pair[0].TryGetInt64(out var timestamp)) return true;
                var date = DateOnly.FromDateTime(
                    DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime);
                return date >= request.From && date <= request.To;
            });
            var points = CoinGeckoMapper.Map(
                payload.Utf8Text, request.AssetId, request.From, request.To,
                request.SourceId, payload.Sha256, payload.Bytes.Length);
            var rejectedCount = Math.Max(0, rawItemCount - points.Count);

            logger.LogInformation(
                "CoinGecko {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
                request.AssetSymbol, points.Count, request.From, request.To);

            return AdapterCompleteness.Price(request, points, rawItemCount, rejectedCount);
        }
        catch (ProviderContractException ex)
        {
            logger.LogError("CoinGecko provider contract rejected: {Code} {Symbol}",
                ex.Code, request.AssetSymbol);
            return AdapterOutcome<PricePoint>.PermanentFailure(ex.Code);
        }
        catch (JsonException)
        {
            // Bozuk payload "veri yok" olarak yumuşatıldığında ingestion_jobs success
            // olarak işaretlenir ve aynı pencere bir daha denenmez — upstream contract
            // değişiklikleri sessizce kaybolur. EVDS adaptörüyle paritede LogError +
            // ExternalApiException ile yukarı bildir; ingestion runner fail olarak
            // işaretler ve operasyon ekibi haberdar olur.
            logger.LogError("CoinGecko JSON çözümlenemedi: {Symbol} ({From}–{To})",
                request.AssetSymbol, request.From, request.To);
            return AdapterOutcome<PricePoint>.PermanentFailure("parse_error");
        }
        catch (HttpRequestException)
        {
            // PR #11 follow-up: Polly retry tükendiğinde orijinal exception stack
            // log'a düşmeden ExternalApiException sarmalanıyordu. Operasyon ekibinin
            // root-cause analizi için inner stack ve adapter context'i loglanır.
            logger.LogError("CoinGecko veri alınamadı: {Source} {Symbol} ({From}–{To})",
                Source, request.AssetSymbol, request.From, request.To);
            return AdapterOutcome<PricePoint>.RetryableFailure("network_error");
        }
    }
}
