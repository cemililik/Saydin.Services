using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;

using Saydin.Shared.Constants;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

public sealed class AssetService(
    IPriceRepository repository,
    IRedisCacheHelper cache,
    IAssetSymbolIndex symbolIndex,
    IAssetNameLocalizer assetNameLocalizer,
    TimeProvider timeProvider,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<AssetService> logger) : IAssetService
{
    // IAssetService is scoped. Successful identities are therefore shared only by
    // calls in the same request and cannot become a cross-request catalog cache.
    // Per-symbol gates coalesce concurrent cold lookups; a cancelled/failed loader
    // never populates the memo and the next waiter performs its own cancellable read.
    private readonly ConcurrentDictionary<string, AssetReadIdentity> _trustedAssetIdentities =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _trustedAssetIdentityGates =
        new(StringComparer.Ordinal);
    private readonly object _catalogVersionGate = new();
    private Task<AssetCatalogVersion>? _catalogVersionTask;

    public Task<AssetCatalogVersion> GetCatalogVersionAsync(CancellationToken ct)
    {
        lock (_catalogVersionGate)
            return _catalogVersionTask ??= LoadCatalogVersionAsync(ct);
    }

    private async Task<AssetCatalogVersion> LoadCatalogVersionAsync(CancellationToken ct)
    {
        var version = await repository.GetAssetCatalogVersionAsync(ct);
        return version.IsValid
            ? version
            : throw new InvalidOperationException("asset_catalog_version_invalid");
    }

    public async Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct)
    {
        var catalog = await GetCatalogVersionAsync(ct);
        var listKey = CatalogKey(catalog, "assets:list");
        var cached = await cache.TryGetAsync<AssetListCacheEntry>(listKey, ct);
        if (cached is not null)
        {
            if (cached.IsValid(catalog)) return cached.Assets!;
            await cache.TryDeleteAsync(listKey, ct);
        }

        var assets = await repository.GetAllActiveAssetsAsync(ct);
        var entry = new AssetListCacheEntry(
            catalog.Revision,
            Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant(),
            assets);
        if (!entry.IsValid(catalog))
            throw new InvalidOperationException("asset_list_cache_contract_invalid");
        await cache.TrySetAsync(listKey, entry, TimeSpan.FromHours(6), ct);

        return assets;
    }

    public async Task<Asset?> GetBySymbolAsync(string symbol, CancellationToken ct)
    {
        // F2.2-20: O(1) sembol lookup. SVCR-001/002/003 follow-up: static field
        // yerine `IAssetSymbolIndex` singleton; immutable record snapshot ile
        // atomik swap; cache key listenin **içerik hash**'ine bağlı (sadece count
        // değil, DisplayName/Category/IsActive değişimleri de invalidate eder).
        var catalog = await GetCatalogVersionAsync(ct);
        var all = await GetAllAsync(ct);
        return symbolIndex.Lookup(all, symbol, catalog);
    }

    public async Task<IReadOnlyList<AssetResponse>> GetAllAssetInfoAsync(CancellationToken ct)
    {
        var catalog = await GetCatalogVersionAsync(ct);
        var identities = await repository.GetAllActiveAssetIdentitiesAsync(ct);
        var sig = identities.Count.ToString(CultureInfo.InvariantCulture);

        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var listKey = CatalogKey(catalog, $"assets:info:{lang}");
        var cached = await cache.TryGetAsync<AssetInfoCacheEntry>(listKey, ct);
        if (cached is not null)
        {
            if (cached.IsValid(sig, lang, identities, catalog)) return cached.Assets!;
            await cache.TryDeleteAsync(listKey, ct);
        }

        var rows = await repository.GetAllActiveAssetsWithDateRangesAsync(ct);
        var result = rows
            .Select(r => new AssetResponse(
                r.Asset.Symbol,
                assetNameLocalizer.Localize(r.Asset.Symbol, r.Asset.DisplayName),
                // F2.3-7: enum sızıntısı kalktı — DTO string snake_case taşır.
                ToSnakeCase(r.Asset.Category),
                r.FirstDate,
                r.LastDate))
            .ToList();

        var entry = new AssetInfoCacheEntry(
            sig,
            lang,
            identities,
            result,
            catalog.Revision,
            Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant());
        if (entry.IsValid(sig, lang, identities, catalog))
            await cache.TrySetAsync(listKey, entry, TimeSpan.FromHours(1), ct);
        return result;
    }

    /// <summary>
    /// F2.3-7 / SVCR-017: PascalCase enum adını snake_case JSON değerine çevirir
    /// (<c>PreciousMetal → precious_metal</c>). .NET built-in
    /// <see cref="JsonNamingPolicy.SnakeCaseLower"/> ile birebir tutarlı — önceki
    /// elle yazılmış StringBuilder loop'u kalktı.
    /// </summary>
    private static string ToSnakeCase(AssetCategory category) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(category.ToString());

    public async Task<PricePoint> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        var catalog = await GetCatalogVersionAsync(ct);
        var identity = await GetTrustedAssetIdentityAsync(symbol, date, ct);
        var cacheKey = CatalogKey(catalog, $"price:{identity.Symbol}:{date:yyyy-MM-dd}");

        var cached = await cache.TryGetAsync<PriceCacheEntry>(cacheKey, ct);
        if (cached is not null && cached.IsValidExact(identity, date, catalog))
            return cached.Point!;
        if (cached is not null)
            await cache.TryDeleteAsync(cacheKey, ct);

        var price = await repository.GetPriceAsync(identity.Symbol, date, ct);

        if (price is null || !FinalObservationAuthority.IsCompleteFinal(price))
            throw new PriceNotFoundException(symbol, date);

        var entry = PriceCacheEntry.Exact(identity, date, price, catalog);
        if (!entry.IsValidExact(identity, date, catalog))
            throw new InvalidOperationException("price_cache_identity_invalid");

        await cache.TrySetAsync(
            cacheKey, entry, TimeSpan.FromHours(24), ct);

        return price;
    }

    public async Task<PricePoint> GetNearestPriceAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        const int MaxDays = 7;
        var catalog = await GetCatalogVersionAsync(ct);
        var identity = await GetTrustedAssetIdentityAsync(symbol, date, ct);

        // Cache: nearest-price:{symbol}:{date} — 24 saat (işlem günleri değişmez)
        var cacheKey = CatalogKey(
            catalog, $"nearest-price:{identity.Symbol}:{date:yyyy-MM-dd}");

        var cached = await cache.TryGetAsync<PriceCacheEntry>(cacheKey, ct);
        if (cached is not null && cached.IsValidNearest(identity, date, MaxDays, catalog))
            return cached.Point!;
        if (cached is not null)
            await cache.TryDeleteAsync(cacheKey, ct);

        var price = await repository.GetNearestPriceAsync(identity.Symbol, date, MaxDays, ct)
            ?? throw new PriceNotFoundException(symbol, date);

        if (!FinalObservationAuthority.IsCompleteFinal(price))
            throw new PriceNotFoundException(symbol, date);

        var entry = PriceCacheEntry.Nearest(identity, date, MaxDays, price, catalog);
        if (!entry.IsValidNearest(identity, date, MaxDays, catalog))
            throw new InvalidOperationException("nearest_price_cache_identity_invalid");

        await cache.TrySetAsync(
            cacheKey, entry,
            TimeSpan.FromHours(24), ct);
        return price;
    }

    public async Task<IReadOnlyList<PricePoint?>> GetNearestPricesAsync(
        string symbol,
        IReadOnlyList<DateOnly> dates,
        CancellationToken ct)
    {
        const int MaxDays = 7;
        const int MaxRequests = 601;

        if (dates.Count is < 1 or > MaxRequests)
            throw new ArgumentOutOfRangeException(nameof(dates));

        // The identity lookup is deliberately performed once. DCA already owns a
        // response-level cache, so per-date cache probes here would reintroduce the
        // same O(n) network/query shape this bulk boundary exists to remove.
        var identity = await GetTrustedAssetIdentityAsync(symbol, dates[0], ct);
        var prices = await repository.GetNearestPricesAsync(identity.Symbol, dates, MaxDays, ct);

        if (prices.Count != dates.Count)
            throw new InvalidOperationException("nearest_price_batch_cardinality_invalid");

        var result = new PricePoint?[prices.Count];
        for (var index = 0; index < prices.Count; index++)
        {
            var point = prices[index];
            result[index] = point is not null && FinalObservationAuthority.IsCompleteFinal(point)
                ? point
                : null;
        }

        return result;
    }

    public async Task<DateOnly> GetLatestPriceDateAsync(string symbol, CancellationToken ct)
    {
        var catalog = await GetCatalogVersionAsync(ct);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var identity = await GetTrustedAssetIdentityAsync(symbol, today, ct);
        var cacheKey = CatalogKey(catalog, $"latest-date:{identity.Symbol}");

        var cached = await cache.TryGetAsync<LatestPriceDateCacheEntry>(cacheKey, ct);
        if (cached is not null && cached.IsValid(identity, catalog))
            return cached.Date;
        if (cached is not null)
            await cache.TryDeleteAsync(cacheKey, ct);

        var date = await repository.GetLatestPriceDateAsync(identity.Symbol, ct)
            ?? throw new PriceNotFoundException(symbol, today);

        await cache.TrySetAsync(
            cacheKey,
            new LatestPriceDateCacheEntry(
                identity.Symbol,
                identity.Source,
                identity.Id,
                date,
                catalog.Revision,
                Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant()),
            TimeSpan.FromHours(1), ct);

        return date;
    }

    public async Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, string interval, CancellationToken ct)
    {
        // Yalnız 'daily' destekleniyor; weekly/monthly future enhancement (PriceIntervals).
        // Sessizce 'daily' döndürmek yerine açıkça reddet — sessiz kontrat ihlali (review H-9).
        // F3.1-2: desteklenen interval kümesi tek noktadan (PriceIntervals.SupportedLookup) gelir.
        var normalizedInterval = interval.Trim().ToLowerInvariant();
        if (!PriceIntervals.SupportedLookup.Contains(normalizedInterval))
            throw new ValidationException(
                string.Format(localizer["InvalidInterval"], interval),
                field: nameof(interval));

        // F2.2-1 ([C-B-AssetService-5/7]): cache key'e interval suffix ekle. Şu an
        // yalnızca "daily" destekleniyor; weekly/monthly eklenirse aynı (symbol, from, to)
        // çiftinin farklı interval'larda farklı response'u olur → cache key ayrımı şart.
        var catalog = await GetCatalogVersionAsync(ct);
        var identity = await GetTrustedAssetIdentityAsync(symbol, from, ct);
        var cacheKey = CatalogKey(
            catalog,
            $"prices:{identity.Symbol}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{normalizedInterval}");

        var cached = await cache.TryGetAsync<PriceRangeCacheEntry>(cacheKey, ct);
        if (cached is not null && cached.IsValid(identity, from, to, normalizedInterval, catalog))
            return cached.Points!.Cast<PricePoint>().ToArray();
        if (cached is not null)
            await cache.TryDeleteAsync(cacheKey, ct);

        var points = await repository.GetPriceRangeAsync(identity.Symbol, from, to, ct);

        if (points.Any(point => !FinalObservationAuthority.IsCompleteFinal(point)))
            throw new InvalidOperationException("price_authority_not_final");

        await cache.TrySetAsync(
            cacheKey,
            PriceRangeCacheEntry.Create(identity, from, to, normalizedInterval, points, catalog),
            TimeSpan.FromHours(1), ct);

        // CLAUDE.md: "LogDebug yalnızca Development ortamında, detay bilgi".
        // Production minimum log seviyesi Information olduğu için bu zaten no-op; ama
        // IsEnabled check'i ile string interpolation/boxing maliyeti tamamen sıfırlanır.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("GetPriceRange dönüyor: {Symbol} {From}-{To} ({Count} nokta)",
                symbol, from, to, points.Count);

        return points;
    }

    private static string CatalogKey(AssetCatalogVersion catalog, string suffix)
        => AuthorityCacheNamespace.Key($"catalog:{catalog.Token}:{suffix}");

    private async Task<AssetReadIdentity> GetTrustedAssetIdentityAsync(
        string symbol,
        DateOnly errorDate,
        CancellationToken ct)
    {
        var upperSymbol = symbol.ToUpperInvariant();
        if (_trustedAssetIdentities.TryGetValue(upperSymbol, out var cached))
            return cached;

        var gate = _trustedAssetIdentityGates.GetOrAdd(
            upperSymbol, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_trustedAssetIdentities.TryGetValue(upperSymbol, out cached))
                return cached;

            var identity = await repository.GetActiveAssetIdentityAsync(upperSymbol, ct)
                ?? throw new PriceNotFoundException(symbol, errorDate);
            if (identity.Id == Guid.Empty
                || !string.Equals(identity.Symbol, upperSymbol, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(identity.Source))
            {
                throw new InvalidOperationException("asset_read_identity_invalid");
            }

            _trustedAssetIdentities.TryAdd(upperSymbol, identity);
            return identity;
        }
        finally
        {
            gate.Release();
        }
    }
}
