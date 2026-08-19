using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;
using System.Globalization;
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

    public async Task<AdapterOutcome<PricePoint>> FetchRangeAsync(
        PriceFetchRequest request,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("tcmb");
        var results = new List<PricePoint>();
        var noDataDates = request.CalendarClosedDates.ToHashSet();
        var xmlCurrencyCode = ExtractCurrencyCode(request.SourceId);
        var rawItemCount = 0;

        try
        {
            for (var date = request.From; date <= request.To; date = date.AddDays(1))
            {
                if (request.CalendarClosedDates.Contains(date))
                    continue;

                var day = await FetchSingleDayAsync(
                    client, request.AssetId, xmlCurrencyCode, date, ct);
                if (day.ExpectedNoData)
                    return AdapterOutcome<PricePoint>.PermanentFailure(
                        "unexpected_404", $"date={date:yyyy-MM-dd}");

                rawItemCount++;
                if (day.Point is null)
                    return AdapterOutcome<PricePoint>.PermanentFailure(
                        day.ErrorCode ?? "schema_missing_currency", rawItemCount: rawItemCount,
                        rejectedCount: 1);
                results.Add(day.Point);
            }
        }
        catch (HttpRequestException)
        {
            logger.LogError("TCMB network/HTTP hatası: {CurrencyCode}", xmlCurrencyCode);
            return AdapterOutcome<PricePoint>.RetryableFailure("network_or_http");
        }
        catch (ProviderContractException ex)
        {
            logger.LogError("TCMB provider contract rejected: {Code} {CurrencyCode}",
                ex.Code, xmlCurrencyCode);
            return AdapterOutcome<PricePoint>.PermanentFailure(ex.Code);
        }
        catch (XmlException)
        {
            logger.LogError("TCMB XML parse hatası: {CurrencyCode}", xmlCurrencyCode);
            return AdapterOutcome<PricePoint>.PermanentFailure("parse_error");
        }

        PurgeExpiredCacheEntries();

        logger.LogInformation(
            "TCMB {CurrencyCode}: {Count} fiyat noktası alındı ({From}–{To})",
            request.SourceId, results.Count, request.From, request.To);

        return AdapterCompleteness.Price(
            request, results.AsReadOnly(), rawItemCount, providerNoDataDates: noDataDates,
            noDataCode: "market_closed");
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

    private async Task<DayFetch> FetchSingleDayAsync(
        HttpClient client,
        Guid assetId,
        string xmlCurrencyCode,
        DateOnly date,
        CancellationToken ct)
    {
        var payload = await GetOrFetchDayXmlAsync(client, date, ct);
        if (payload is null) return new DayFetch(null, true, null); // provider-confirmed no publication

        try
        {
            var point = TcmbMapper.Map(
                payload.Document, assetId, xmlCurrencyCode, date,
                payload.PayloadSha256, payload.PayloadByteLength);
            return point is null
                ? new DayFetch(null, false, "schema_missing_currency")
                : new DayFetch(point, false, null);
        }
        catch (FormatException)
        {
            logger.LogError("TCMB kur değeri parse edilemedi: {Date} {CurrencyCode}", date, xmlCurrencyCode);
            return new DayFetch(null, false, "value_parse_error");
        }
    }

    private async Task<DayPayload?> GetOrFetchDayXmlAsync(
        HttpClient client, DateOnly date, CancellationToken ct)
    {
        // Race-free single-flight: ConcurrentDictionary.GetOrAdd contention altında
        // factory'yi birden fazla kez çağırabilir; eski sürümde her invocation eager
        // olarak FetchDayXmlAsync'i tetikliyor, kaybeden Task'lar leak oluyordu.
        // Lazy<Task<>> ile fetch yalnızca kazanan entry'nin .Value erişiminde başlar
        // (LazyThreadSafetyMode.ExecutionAndPublication default).
        var entry = _dayCache.GetOrAdd(date, d => new CachedXmlEntry(
            new Lazy<Task<DayPayload?>>(() => FetchDayXmlAsync(client, d, ct)),
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

    private async Task<DayPayload?> FetchDayXmlAsync(
        HttpClient client, DateOnly date, CancellationToken ct)
    {
        // URL formatı: YYYYMM/DDMMYYYY.xml (base address ile birleşir)
        var url = $"{date.ToString("yyyyMM", CultureInfo.InvariantCulture)}/{date.ToString("ddMMyyyy", CultureInfo.InvariantCulture)}.xml";

        using var response = await client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Resmi tatil günü — TCMB o gün için dosya yayınlamaz; normal durum
            logger.LogDebug("TCMB resmi tatil veya veri yok: {Date}", date);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var payload = await BoundedHttpContent.ReadAsync(response.Content, ct).ConfigureAwait(false);
        // P1R-002: parse'ı cache'in iç tarafında bir kez yap. Çağıran sembolün
        // sayısı kadar XDocument.Parse çağırmaktan kaçınılır.
        using var stream = new MemoryStream(payload.Bytes, writable: false);
        return new DayPayload(XDocument.Load(stream), payload.Sha256, payload.Bytes.Length);
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

    private sealed record CachedXmlEntry(Lazy<Task<DayPayload?>> XmlTaskLazy, DateTime CreatedAtUtc);
    private sealed record DayPayload(
        XDocument Document, byte[] PayloadSha256, int PayloadByteLength);
    private sealed record DayFetch(PricePoint? Point, bool ExpectedNoData, string? ErrorCode);
}
