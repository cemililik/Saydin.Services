using System.Collections.Concurrent;
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

    private sealed record CachedJson(string Json, DateTimeOffset CachedAt);

    public async Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        Guid assetId, string assetSymbol, string sourceId,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var appId = configuration["ExternalApis:OpenExchangeRates:AppId"];
        if (string.IsNullOrWhiteSpace(appId))
        {
            logger.LogWarning("OpenExchangeRates AppId yapılandırılmamış, {Symbol} atlandı", assetSymbol);
            return [];
        }

        var client = httpClientFactory.CreateClient("openexchangerates");
        var results = new List<PricePoint>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (ct.IsCancellationRequested) break;

            var point = await FetchDayAsync(client, assetId, sourceId, date, appId, ct);
            if (point is not null)
                results.Add(point);

            // Cache'te olmayan günler için HTTP isteği yapılır; küçük bekleme ile
            // rate limit (1000/ay ≈ 33/gün) aşılmaz.
            await Task.Delay(200, ct);
        }

        logger.LogInformation(
            "OpenExchangeRates {Symbol}: {Count} fiyat noktası alındı ({From}–{To})",
            assetSymbol, results.Count, from, to);

        return results.AsReadOnly();
    }

    private async Task<PricePoint?> FetchDayAsync(
        HttpClient client, Guid assetId, string metalCode,
        DateOnly date, string appId, CancellationToken ct)
    {
        try
        {
            var json = await GetOrFetchJsonAsync(client, date, appId, ct);
            if (json is null) return null;

            return OpenExchangeRatesMapper.Map(json, assetId, date, metalCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenExchangeRates veri alınamadı: {Metal} {Date}", metalCode, date);
            return null;
        }
    }

    private async Task<string?> GetOrFetchJsonAsync(HttpClient client, DateOnly date, string appId, CancellationToken ct)
    {
        // F2.4-4: TTL kontrolü ile cache lookup. Süresi dolmuşsa cache'i bypass et,
        // taze yanıtla yenile.
        if (_dayCache.TryGetValue(date, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAt < EntryTtl)
            return cached.Json;

        // XAU, XAG ve TRY'yi tek istekte çek
        var url = $"historical/{date:yyyy-MM-dd}.json?app_id={appId}&symbols=XAU,XAG,TRY";

        using var response = await client.GetAsync(url, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            logger.LogWarning(
                "OpenExchangeRates {StatusCode}: API key geçersiz veya limit aşıldı (ExternalApis:OpenExchangeRates:AppId).",
                (int)response.StatusCode);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        _dayCache[date] = new CachedJson(json, DateTimeOffset.UtcNow);

        // F2.4-4: sınırı aştığımızda en eski yarıyı atarak temel LRU-benzeri davranış.
        // Bellek tüketimi sabit kalır, hot-set canlı kalır.
        if (_dayCache.Count > MaxEntries)
            EvictOldestHalf();
        return json;
    }

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
}
