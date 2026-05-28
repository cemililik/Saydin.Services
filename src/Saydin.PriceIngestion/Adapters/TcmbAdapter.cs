using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;
using Saydin.PriceIngestion.Mappers;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Adapters;

/// <summary>
/// TCMB (Türkiye Cumhuriyet Merkez Bankası) döviz kuru adaptörü.
/// API key gerektirmez. Hafta içi 16:00'dan itibaren güncel kurlar yayınlanır.
/// Endpoint: https://www.tcmb.gov.tr/kurlar/{YYYYMM}/{DDMMYYYY}.xml
///
/// F1.1-2: TCMB tek günlük XML tüm para birimlerini içerir — backfill esnasında
/// aynı gün için her sembolde tekrar HTTP istek atmak yerine 5 dakikalık day-level
/// cache uygulanır (20 yıl × 30 sembol = ~150k HTTP isteği yerine ~5200).
/// </summary>
public sealed class TcmbAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<TcmbAdapter> logger) : IExternalPriceAdapter
{
    public string Source => "tcmb";

    // F1.1-2: gün-bazlı XDocument cache. SemaphoreSlim/once-per-day fetch eşzamanlılık
    // koruması için AsyncLazy + ConcurrentDictionary tercih edildi: ilk symbol için
    // fetch çalışırken aynı gün için bekleyen diğer symbol'lerin tetiklediği fetch'ler
    // aynı Task.Result'ı paylaşır. XML metni yerine **parse edilmiş XDocument**
    // tutulur — aynı günün 30 sembolünde XDocument.Parse 30 değil 1 kez çalışır
    // (review P1R-002: parse-once optimizasyonu).
    private readonly ConcurrentDictionary<DateOnly, CachedXmlEntry> _dayCache = new();
    // 20 yıllık backfill yaklaşık 5200 iş günü XML üretir (5KB × 5200 ≈ 26MB).
    // 60 dakika TTL aynı backfill cycle içinde tüm sembollere yetecek cache hit'i sağlar;
    // sonraki günlük refresh sadece T0 XML'ini fetch ettiği için cache büyümez.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);
    private const int MaxCacheEntries = 10_000;

    public async Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        Guid assetId,
        string assetSymbol,
        string sourceId,       // ISO 4217 kodu: "USD", "EUR"
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("tcmb");
        var results = new List<PricePoint>();
        var xmlCurrencyCode = ExtractCurrencyCode(sourceId);

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            // TCMB hafta sonu yayın yapmaz
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            var point = await FetchSingleDayAsync(client, assetId, xmlCurrencyCode, date, ct);
            if (point is not null)
                results.Add(point);
        }

        PurgeExpiredCacheEntries();

        logger.LogInformation(
            "TCMB {CurrencyCode}: {Count} fiyat noktası alındı ({From}–{To})",
            sourceId, results.Count, from, to);

        return results.AsReadOnly();
    }

    private static string ExtractCurrencyCode(string sourceId)
    {
        // TCMB series kodu "TP.DK.USD.A" → XML'deki CurrencyCode "USD".
        // Defansif: sourceId DB/config'den geldiği için "USD.", "TP.USD" gibi eksik
        // segmentli değerler beklenmeyen IndexOutOfRangeException üretmemeli — bu
        // durumda ham değeri olduğu gibi kullan (assets.source_id zaten Asset entity
        // konfigürasyonunda valide ediliyor, bu sadece extra savunma katmanı).
        var segments = sourceId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 ? segments[2] : sourceId;
    }

    private async Task<PricePoint?> FetchSingleDayAsync(
        HttpClient client,
        Guid assetId,
        string xmlCurrencyCode,
        DateOnly date,
        CancellationToken ct)
    {
        XDocument? doc;
        try
        {
            doc = await GetOrFetchDayXmlAsync(client, date, ct);
        }
        catch (HttpRequestException ex)
        {
            // Polly retry zinciri tükendi; range fetch'i ingestion_jobs failed olarak
            // işaretlensin diye yukarı fırlat (review F1.1-3). ExternalApiException
            // sarmalaması inner stack'i operasyon ekibine göstermez, bu yüzden
            // orijinal hata burada loglanır (CoinGecko/TwelveData ile paritede).
            logger.LogError(ex,
                "TCMB veri alınamadı: {Source} {Date} {CurrencyCode}",
                Source, date, xmlCurrencyCode);
            throw new ExternalApiException(Source,
                $"TCMB XML alınamadı ({date:yyyy-MM-dd} {xmlCurrencyCode})", ex);
        }
        catch (XmlException ex)
        {
            // Cache fetch'inde bozuk XML — gün boyunca tüm semboller için "veri yok"
            // sayılır (parse-once: aynı XDocument tüm sembollere fail eder).
            logger.LogWarning(ex, "TCMB XML çözümlenemedi: {Date} {CurrencyCode}", date, xmlCurrencyCode);
            return null;
        }

        if (doc is null) return null;  // 404 — TCMB resmi tatil

        try
        {
            return TcmbMapper.Map(doc, assetId, xmlCurrencyCode, date);
        }
        catch (FormatException ex)
        {
            // Mapper'da kur ondalığı parse hatası — yine veri yok olarak işle.
            logger.LogWarning(ex, "TCMB kur değeri parse edilemedi: {Date} {CurrencyCode}", date, xmlCurrencyCode);
            return null;
        }
    }

    private async Task<XDocument?> GetOrFetchDayXmlAsync(
        HttpClient client, DateOnly date, CancellationToken ct)
    {
        // Race-free single-flight: ConcurrentDictionary.GetOrAdd contention altında
        // factory'yi birden fazla kez çağırabilir; eski sürümde her invocation eager
        // olarak FetchDayXmlAsync'i tetikliyor, kaybeden Task'lar leak oluyordu.
        // Lazy<Task<>> ile fetch yalnızca kazanan entry'nin .Value erişiminde başlar
        // (LazyThreadSafetyMode.ExecutionAndPublication default).
        var entry = _dayCache.GetOrAdd(date, d => new CachedXmlEntry(
            new Lazy<Task<XDocument?>>(() => FetchDayXmlAsync(client, d, ct)),
            DateTime.UtcNow));

        try
        {
            return await entry.XmlTaskLazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Hata sonucunu cache'te tutma — bir sonraki istek tekrar denesin.
            _dayCache.TryRemove(date, out _);
            throw;
        }
    }

    private async Task<XDocument?> FetchDayXmlAsync(
        HttpClient client, DateOnly date, CancellationToken ct)
    {
        // URL formatı: YYYYMM/DDMMYYYY.xml (base address ile birleşir)
        var url = $"{date:yyyyMM}/{date:ddMMyyyy}.xml";

        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Resmi tatil günü — TCMB o gün için dosya yayınlamaz; normal durum
            logger.LogDebug("TCMB resmi tatil veya veri yok: {Date}", date);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // P1R-002: parse'ı cache'in iç tarafında bir kez yap. Çağıran sembolün
        // sayısı kadar XDocument.Parse çağırmaktan kaçınılır.
        return XDocument.Parse(xml);
    }

    private void PurgeExpiredCacheEntries()
    {
        var threshold = DateTime.UtcNow - CacheTtl;
        foreach (var kv in _dayCache)
        {
            if (kv.Value.CreatedAtUtc < threshold)
                _dayCache.TryRemove(kv.Key, out _);
        }

        // Defansif maksimum kapasite: TTL bypass durumunda (ör. saatleri geçerken)
        // bellek sızıntısını engelle. En eski 25%'yi at.
        if (_dayCache.Count > MaxCacheEntries)
        {
            var toRemove = _dayCache
                .OrderBy(kv => kv.Value.CreatedAtUtc)
                .Take(_dayCache.Count - (MaxCacheEntries * 3 / 4))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in toRemove)
                _dayCache.TryRemove(key, out _);
        }
    }

    private sealed record CachedXmlEntry(Lazy<Task<XDocument?>> XmlTaskLazy, DateTime CreatedAtUtc);
}
