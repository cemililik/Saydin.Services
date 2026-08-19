using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Globalization;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

/// <summary>
/// Open Exchange Rates (openexchangerates.org) üzerinden XAU/XAG fiyatlarını çeker.
/// Free plan: 1.000 istek/ay, USD base, her tarih için tek istek yeterlidir.
///
/// Günlük cache: XAU ve XAG aynı HTTP yanıtından okunur.
/// Backfill sırasında XAU için alınan yanıt önbelleğe alınır, XAG tekrar HTTP isteği yapmaz.
/// </summary>
public sealed class OpenExchangeRatesAdapter(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<OpenExchangeRatesAdapter> logger) : IExternalPriceAdapter
{
    public string Source => "openexchangerates";

    // F2.4-4 ([C-D-17]): Gün bazlı in-memory cache. Singleton adapter olduğu için
    // backfill boyunca büyür — sınırsız büyümeyi engellemek için iki sınır:
    //   1) Entry-level TTL (24 saat) — tarihsel kur değişmez ama process restart'lar
    //      arasında bellek tüketimi bağlı kalmasın.
    //   2) Toplam entry sınırı (MaxEntries) — sınır aşılınca en eski yarısı atılır.
    // Backfill tipik olarak gün-tarafsızdır (XAU + XAG aynı yanıttan); 7300 entry =
    // ~20 yıl backfill. 10k limit emniyet katmanı.
    private const int MaxEntries = 10_000;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<DateOnly, CachedJson> _dayCache = new();

    private sealed record CachedJson(string Json, byte[] PayloadSha256, DateTimeOffset CachedAt);

    public async Task<AdapterOutcome<PricePoint>> FetchRangeAsync(
        PriceFetchRequest request, CancellationToken ct)
    {
        var appId = configuration["ExternalApis:OpenExchangeRates:AppId"];
        if (string.IsNullOrWhiteSpace(appId))
        {
            logger.LogError("OpenExchangeRates AppId yapılandırılmamış: {Symbol}", request.AssetSymbol);
            return AdapterOutcome<PricePoint>.PermanentFailure("auth_missing_app_id");
        }

        var completedUtcDay = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime.Date.AddDays(-1));
        if (request.To > completedUtcDay)
            return AdapterOutcome<PricePoint>.RetryableFailure("current_day_provisional");

        var client = httpClientFactory.CreateClient("openexchangerates");
        var results = new List<PricePoint>();
        var remoteRequestIssued = false;

        for (var date = request.From; date <= request.To; date = date.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();

            var day = await GetOrFetchJsonAsync(
                client, date, appId, remoteRequestIssued, ct);
            if (day.RemoteRequestIssued)
                remoteRequestIssued = true;
            if (day.FailureKind is { } kind)
                return kind == AdapterOutcomeKind.RetryableFailure
                    ? AdapterOutcome<PricePoint>.RetryableFailure(day.Code, day.Detail)
                    : AdapterOutcome<PricePoint>.PermanentFailure(day.Code, day.Detail);

            PricePoint? point;
            try
            {
                point = OpenExchangeRatesMapper.Map(
                    day.Json!, request.AssetId, date, request.SourceId, day.PayloadSha256);
            }
            catch (JsonException ex)
            {
                return results.Count == 0
                    ? AdapterOutcome<PricePoint>.PermanentFailure(
                        "parse_error", ex.GetType().Name, rawItemCount: 1, rejectedCount: 1)
                    : AdapterOutcome<PricePoint>.PartialRejected(
                        results.AsReadOnly(), results.Count + 1, 1, "parse_error", ex.GetType().Name);
            }
            catch (ProviderContractException ex)
            {
                return results.Count == 0
                    ? AdapterOutcome<PricePoint>.PermanentFailure(
                        ex.Code, rawItemCount: 1, rejectedCount: 1)
                    : AdapterOutcome<PricePoint>.PartialRejected(
                        results.AsReadOnly(), results.Count + 1, 1, ex.Code);
            }
            if (point is null)
                return results.Count == 0
                    ? AdapterOutcome<PricePoint>.PermanentFailure(
                        "contract_missing_rate", $"metal={request.SourceId};date={date:yyyy-MM-dd}",
                        rawItemCount: 1, rejectedCount: 1)
                    : AdapterOutcome<PricePoint>.PartialRejected(
                        results.AsReadOnly(), results.Count + 1, 1, "contract_missing_rate",
                        $"metal={request.SourceId};date={date:yyyy-MM-dd}");
            results.Add(point);
        }

        logger.LogInformation(
            "OpenExchangeRates {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
            request.AssetSymbol, results.Count, request.From, request.To);

        return AdapterCompleteness.Price(request, results.AsReadOnly(), results.Count);
    }

    private async Task<JsonFetch> GetOrFetchJsonAsync(
        HttpClient client,
        DateOnly date,
        string appId,
        bool delayBeforeRemoteRequest,
        CancellationToken ct)
    {
        // F2.4-4: TTL kontrolü ile cache lookup. Süresi dolmuşsa cache'i bypass et,
        // taze yanıtla yenile.
        if (_dayCache.TryGetValue(date, out var cached))
        {
            if (timeProvider.GetUtcNow() - cached.CachedAt < EntryTtl)
                return new JsonFetch(
                    cached.Json, cached.PayloadSha256, null, "data_complete", null);
            // INGR-009: TTL miss — stale entry'yi sil; MaxEntries threshold'una
            // stale entry'lerin katkıda bulunmaması için.
            _dayCache.TryRemove(date, out _);
        }

        // Gecikme yalnız aynı logical fetch içindeki gerçek provider isteklerinin
        // arasındadır. Cache hit'leri ve son remote istekten sonrası beklemez.
        if (delayBeforeRemoteRequest)
            await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, ct);

        // XAU, XAG ve TRY'yi tek istekte çek
        var url = $"historical/{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json?symbols=XAU,XAG,TRY&prettyprint=false";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", appId);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("OpenExchangeRates network error: {ExceptionType}", ex.GetType().Name);
            return new JsonFetch(null, null, AdapterOutcomeKind.RetryableFailure,
                "network_error", null);
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status >= 500)
                return new JsonFetch(null, null, AdapterOutcomeKind.RetryableFailure,
                    "http_5xx", $"status={status}");

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var body = await BoundedHttpContent.ReadAsync(response.Content, ct);
                var providerMessage = ReadProviderMessage(body.Bytes);
                return string.Equals(providerMessage, "not_allowed", StringComparison.OrdinalIgnoreCase)
                    ? new JsonFetch(null, null, AdapterOutcomeKind.PermanentFailure,
                        "plan_not_allowed", "status=429")
                    : new JsonFetch(null, null, AdapterOutcomeKind.RetryableFailure,
                        "http_429", "status=429");
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "auth_rejected",
                    System.Net.HttpStatusCode.Forbidden => "access_restricted",
                    System.Net.HttpStatusCode.NotFound => "historical_route_not_found",
                    System.Net.HttpStatusCode.BadRequest => "invalid_or_unavailable_date",
                    _ => "http_4xx",
                };
                return new JsonFetch(null, null, AdapterOutcomeKind.PermanentFailure, code, $"status={status}");
            }

            var payload = await BoundedHttpContent.ReadAsync(response.Content, ct);
            var json = payload.Utf8Text;
            _dayCache[date] = new CachedJson(json, payload.Sha256, timeProvider.GetUtcNow());

            // F2.4-4: sınırı aştığımızda en eski yarıyı atarak temel LRU-benzeri davranış.
            // INGR-008: paralel iki Fetch eviction'ı tetiklerse Interlocked flag ile
            // yalnız bir tanesi geçer; diğeri no-op.
            if (_dayCache.Count > MaxEntries
                && Interlocked.CompareExchange(ref _evicting, 1, 0) == 0)
            {
                try
                {
                    EvictOldestHalf();
                }
                finally
                {
                    Volatile.Write(ref _evicting, 0);
                }
            }
            return new JsonFetch(
                json, payload.Sha256, null, "data_complete", null, RemoteRequestIssued: true);
        }
    }

    private int _evicting;

    private void EvictOldestHalf()
    {
        // CachedAt'a göre sırala ve ilk yarıyı at. ConcurrentDictionary üzerinde tam
        // tutarlı snapshot garantilenmez ama eviction yaklaşık doğrudur (en eski
        // ~%50 atılır); bu mod-bilinçli adaptif değildir, dolayısıyla statistical fairness yeterli.
        var snapshot = _dayCache.ToArray();
        var threshold = snapshot
            .OrderBy(kv => kv.Value.CachedAt)
            .Take(snapshot.Length / 2)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var key in threshold)
            _dayCache.TryRemove(key, out _);
    }

    private sealed record JsonFetch(
        string? Json, byte[]? PayloadSha256,
        AdapterOutcomeKind? FailureKind, string Code, string? Detail,
        bool RemoteRequestIssued = false);

    private static string? ReadProviderMessage(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
