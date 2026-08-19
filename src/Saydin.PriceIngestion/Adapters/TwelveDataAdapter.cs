using System.Text.Json;
using System.Globalization;
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

    public async Task<AdapterOutcome<PricePoint>> FetchRangeAsync(
        PriceFetchRequest request, CancellationToken ct)
    {
        var apiKey = configuration["ExternalApis:TwelveData:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogError("TwelveData API key yapılandırılmamış: {Symbol}", request.AssetSymbol);
            return AdapterOutcome<PricePoint>.PermanentFailure("auth_missing_api_key");
        }

        var client = httpClientFactory.CreateClient("twelvedata");

        // F1.1-5: apikey URL query string'inde değil HTTP header'da gönderilir;
        // aksi halde OTel trace span'larında "Url" attribute olarak secret sızar.
        // TwelveData "Authorization: apikey <key>" header şemasını destekler.
        // F2.4-3: outputsize'ı config'e taşı (default 5000 üst limit).
        var outputSize = configuration.GetValue("ExternalApis:TwelveData:OutputSize", 5000);
        var url = $"time_series?symbol={Uri.EscapeDataString(request.SourceId)}" +
                  $"&interval=1day&start_date={request.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                  $"&end_date={request.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                  $"&outputsize={outputSize}&format=JSON";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"apikey {apiKey}");

        try
        {
            using var response = await client.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning(
                    "TwelveData 429 ({Symbol}) — Polly retry tükendi.", request.AssetSymbol);
                return AdapterOutcome<PricePoint>.RetryableFailure("http_429");
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return status >= 500
                    ? AdapterOutcome<PricePoint>.RetryableFailure("http_5xx", $"status={status}")
                    : AdapterOutcome<PricePoint>.PermanentFailure(
                        response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                            ? "auth_rejected" : "http_4xx", $"status={status}");
            }

            var payload = await BoundedHttpContent.ReadAsync(response.Content, ct);
            using var document = JsonDocument.Parse(payload.Bytes);
            var root = document.RootElement;
            if (root.TryGetProperty("status", out var statusElement)
                && !string.Equals(statusElement.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                var providerCode = root.TryGetProperty("code", out var codeElement)
                    && codeElement.ValueKind == JsonValueKind.Number
                    && codeElement.TryGetInt32(out var numericCode)
                    && numericCode is >= 100 and <= 599
                    ? numericCode : 0;
                return providerCode == 429
                    ? AdapterOutcome<PricePoint>.RetryableFailure("provider_rate_limit")
                    : AdapterOutcome<PricePoint>.PermanentFailure(
                        "provider_error", providerCode == 0 ? "code=unknown" : $"code={providerCode}");
            }
            if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                return AdapterOutcome<PricePoint>.PermanentFailure("schema_missing_values");

            var rawItemCount = values.GetArrayLength();
            var points = TwelveDataMapper.Map(
                payload.Utf8Text, request.AssetId, request.SourceId, Source,
                payload.Sha256, payload.Bytes.Length);
            var rejectedCount = Math.Max(0, rawItemCount - points.Count);

            logger.LogInformation(
                "TwelveData {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
                request.AssetSymbol, points.Count, request.From, request.To);

            return AdapterCompleteness.Price(request, points, rawItemCount, rejectedCount);
        }
        catch (ProviderContractException ex)
        {
            logger.LogError("TwelveData provider contract rejected: {Code} {Symbol}",
                ex.Code, request.AssetSymbol);
            return AdapterOutcome<PricePoint>.PermanentFailure(ex.Code);
        }
        catch (JsonException)
        {
            // EVDS adaptörüyle paritede malformed JSON yumuşatılmaz; ingestion runner
            // fail olarak işaretlesin ve upstream contract değişiklikleri kaybolmasın.
            logger.LogError("TwelveData JSON çözümlenemedi: {Symbol} ({From}–{To})",
                request.AssetSymbol, request.From, request.To);
            return AdapterOutcome<PricePoint>.PermanentFailure("parse_error");
        }
        catch (HttpRequestException)
        {
            // PR #11 follow-up: ExternalApiException sarmalaması inner stack'i
            // operasyon ekibine göstermez; orijinal hata burada loglanmalı.
            logger.LogError("TwelveData veri alınamadı: {Source} {Symbol} ({From}–{To})",
                Source, request.AssetSymbol, request.From, request.To);
            return AdapterOutcome<PricePoint>.RetryableFailure("network_error");
        }
    }
}
