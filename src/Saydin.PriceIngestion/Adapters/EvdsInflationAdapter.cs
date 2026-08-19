using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Constants;
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
    TimeProvider timeProvider,
    ILogger<EvdsInflationAdapter> logger) : IInflationAdapter
{
    private const string SeriesCode = "TP.FG.J0";

    public string Source => "evds";

    public async Task<AdapterOutcome<InflationRate>> FetchRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var apiKey = configuration["ExternalApis:Evds:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogError("EVDS API key yapılandırılmamış. Enflasyon verisi çekilemiyor.");
            return AdapterOutcome<InflationRate>.PermanentFailure("auth_missing_api_key");
        }

        var client = httpClientFactory.CreateClient("evds");

        // EVDS tarih formatı: DD-MM-YYYY; key HTTP header olarak gönderilir (query param değil)
        // frequency=5: aylık; formulas=0: düzey (ham endeks değeri)
        var startDate = from.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        var endDate   = to.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        var url = $"igmevdsms-dis/series={SeriesCode}&startDate={startDate}&endDate={endDate}&type=json&frequency=5&formulas=0";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("key", apiKey);

        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var outcome = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "auth",
                    System.Net.HttpStatusCode.TooManyRequests => "rate_limit",
                    var s when (int)s >= 500 => "http_5xx",
                    _ => "http_4xx",
                };
                logger.LogError(
                    "EVDS TÜFE API hatası {StatusCode} ({Outcome}) ({From}–{To})",
                    statusCode, outcome, from, to);
                SaydinMetrics.InflationIngestionFailures.Add(1,
                    new KeyValuePair<string, object?>("source", Source),
                    new KeyValuePair<string, object?>("outcome", outcome));
                return response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || statusCode >= 500
                    ? AdapterOutcome<InflationRate>.RetryableFailure(outcome, $"status={statusCode}")
                    : AdapterOutcome<InflationRate>.PermanentFailure(outcome, $"status={statusCode}");
            }

            var payload = await BoundedHttpContent.ReadAsync(response.Content, ct);
            using var document = JsonDocument.Parse(payload.Bytes);
            if (!document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
                return AdapterOutcome<InflationRate>.PermanentFailure("schema_missing_items");

            var rawItemCount = items.GetArrayLength();
            // INGR-010: inflation_rates.source veri KÖKENİDİR (TÜİK) — chk_inflation_rates_source
            // yalnız 'tuik'/'seed-approximation' kabul eder. "evds" yalnız taşıma kanalıdır ve
            // ingestion_jobs.source='evds' (EvdsInflationWorker, adapter.Source) ile zaten
            // kaydedilir. TP.FG.J0 serisi TÜİK TÜFE'sidir → InflationSources.Tuik yazılır.
            var rates = EvdsInflationMapper.Map(
                payload.Utf8Text, source: InflationSources.Tuik,
                payloadSha256: payload.Sha256,
                payloadByteLength: payload.Bytes.Length);

            logger.LogInformation(
                "EVDS TÜFE: {Count} aylık endeks alındı ({From}–{To})",
                rates.Count, from, to);

            var expected = ExpectedMonths(from, to).ToHashSet();
            var acceptedDates = rates.Select(rate => rate.PeriodDate).ToArray();
            var accepted = acceptedDates.ToHashSet();
            var duplicateCount = acceptedDates.Length - accepted.Count;
            var outOfRangeCount = accepted.Count(date => !expected.Contains(date));
            var missing = expected.Where(date => !accepted.Contains(date)).ToArray();
            var rejectedCount = Math.Max(0, rawItemCount - rates.Count)
                + duplicateCount + outOfRangeCount;

            if (missing.Length == 0 && rejectedCount == 0)
                return AdapterOutcome<InflationRate>.Data(rates, rawItemCount);

            var utcNow = timeProvider.GetUtcNow().UtcDateTime;
            var lastPublishedTarget = new DateOnly(utcNow.Year, utcNow.Month, 1)
                .AddMonths(-1);
            if (missing.Length == 1 && missing[0] == lastPublishedTarget
                && accepted.All(date => date < lastPublishedTarget))
                return AdapterOutcome<InflationRate>.RetryableFailure(
                    "not_published_yet", rawItemCount: rawItemCount,
                    rejectedCount: Math.Max(1, rejectedCount));

            return AdapterOutcome<InflationRate>.PartialRejected(
                rates, rawItemCount, Math.Max(1, rejectedCount + missing.Length),
                "incomplete_month_set", $"missing={missing.Length}");
        }
        catch (HttpRequestException)
        {
            // Polly retry tükenmiş network hatası.
            // PR #11 follow-up: metric'in yanı sıra exception stack'i de logla;
            // ExternalApiException sarmalaması inner stack'i sızdırmaz.
            logger.LogError("EVDS network hatası: {Source} ({From}–{To})", Source, from, to);
            SaydinMetrics.InflationIngestionFailures.Add(1,
                new KeyValuePair<string, object?>("source", Source),
                new KeyValuePair<string, object?>("outcome", "transient"));
            return AdapterOutcome<InflationRate>.RetryableFailure("network_error");
        }
        catch (JsonException)
        {
            logger.LogError("EVDS yanıtı çözümlenemedi: {Source} ({From}–{To})", Source, from, to);
            SaydinMetrics.InflationIngestionFailures.Add(1,
                new KeyValuePair<string, object?>("source", Source),
                new KeyValuePair<string, object?>("outcome", "parse"));
            return AdapterOutcome<InflationRate>.PermanentFailure("parse_error");
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static IReadOnlyList<DateOnly> ExpectedMonths(DateOnly from, DateOnly to)
    {
        var months = new List<DateOnly>();
        var current = new DateOnly(from.Year, from.Month, 1);
        var end = new DateOnly(to.Year, to.Month, 1);
        while (current <= end)
        {
            months.Add(current);
            current = current.AddMonths(1);
        }
        return months;
    }
}
