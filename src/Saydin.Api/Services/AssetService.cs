using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;

using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

public sealed class AssetService(
    IPriceRepository repository,
    IRedisCacheHelper cache,
    IAssetSymbolIndex symbolIndex,
    IAssetNameLocalizer assetNameLocalizer,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<AssetService> logger) : IAssetService
{
    public async Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct)
    {
        // Signature = aktif asset sayısı. 5 dakikada bir DB'den taze okunur.
        // Yeni asset eklenince sayı değişir → yeni cache key oluşur → otomatik invalidasyon.
        const string sigKey = "assets:sig";

        var sig = await cache.TryGetAsync<string>(sigKey, ct);
        if (sig is null)
        {
            var count = await repository.GetActiveAssetCountAsync(ct);
            sig = count.ToString();
            await cache.TrySetAsync(sigKey, sig, TimeSpan.FromMinutes(5), ct);
        }

        var listKey = $"assets:list:{sig}";
        var cached = await cache.TryGetAsync<List<Asset>>(listKey, ct);
        if (cached is not null) return cached;

        var assets = await repository.GetAllActiveAssetsAsync(ct);
        await cache.TrySetAsync(listKey, assets, TimeSpan.FromHours(6), ct);

        return assets;
    }

    public async Task<Asset?> GetBySymbolAsync(string symbol, CancellationToken ct)
    {
        // F2.2-20: O(1) sembol lookup. SVCR-001/002/003 follow-up: static field
        // yerine `IAssetSymbolIndex` singleton; immutable record snapshot ile
        // atomik swap; cache key listenin **içerik hash**'ine bağlı (sadece count
        // değil, DisplayName/Category/IsActive değişimleri de invalidate eder).
        var all = await GetAllAsync(ct);
        return symbolIndex.Lookup(all, symbol);
    }

    public async Task<IReadOnlyList<AssetResponse>> GetAllAssetInfoAsync(CancellationToken ct)
    {
        const string sigKey = "assets:sig";

        var sig = await cache.TryGetAsync<string>(sigKey, ct);
        if (sig is null)
        {
            var count = await repository.GetActiveAssetCountAsync(ct);
            sig = count.ToString();
            await cache.TrySetAsync(sigKey, sig, TimeSpan.FromMinutes(5), ct);
        }

        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var listKey = $"assets:info:{sig}:{lang}";
        var cached = await cache.TryGetAsync<List<AssetResponse>>(listKey, ct);
        if (cached is not null) return cached;

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

        await cache.TrySetAsync(listKey, result, TimeSpan.FromHours(1), ct);
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
        var cacheKey = $"price:{symbol.ToUpperInvariant()}:{date:yyyy-MM-dd}";

        var cached = await cache.TryGetAsync<PricePoint>(cacheKey, ct);
        if (cached is not null) return cached;

        var price = await repository.GetPriceAsync(symbol.ToUpperInvariant(), date, ct);

        if (price is null)
            throw new PriceNotFoundException(symbol, date);

        await cache.TrySetAsync(cacheKey, price, TimeSpan.FromHours(24), ct);

        return price;
    }

    public async Task<PricePoint> GetNearestPriceAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        const int MaxDays = 7;
        var upperSymbol = symbol.ToUpperInvariant();

        // Cache: nearest-price:{symbol}:{date} — 24 saat (işlem günleri değişmez)
        var cacheKey = $"nearest-price:{upperSymbol}:{date:yyyy-MM-dd}";

        var cached = await cache.TryGetAsync<PricePoint>(cacheKey, ct);
        if (cached is not null) return cached;

        var price = await repository.GetNearestPriceAsync(upperSymbol, date, MaxDays, ct)
            ?? throw new PriceNotFoundException(symbol, date);

        await cache.TrySetAsync(cacheKey, price, TimeSpan.FromHours(24), ct);
        return price;
    }

    public async Task<DateOnly> GetLatestPriceDateAsync(string symbol, CancellationToken ct)
    {
        var cacheKey = $"latest-date:{symbol.ToUpperInvariant()}";

        var cached = await cache.TryGetAsync<string>(cacheKey, ct);
        if (cached is not null && DateOnly.TryParse(cached, CultureInfo.InvariantCulture, out var cachedDate))
            return cachedDate;

        var date = await repository.GetLatestPriceDateAsync(symbol.ToUpperInvariant(), ct)
            ?? throw new PriceNotFoundException(symbol, DateOnly.FromDateTime(DateTime.UtcNow));

        await cache.TrySetAsync(cacheKey, date.ToString("yyyy-MM-dd"), TimeSpan.FromHours(1), ct);

        return date;
    }

    public async Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, string interval, CancellationToken ct)
    {
        // Yalnız 'daily' destekleniyor; weekly/monthly future enhancement.
        // Sessizce 'daily' döndürmek yerine açıkça reddet — sessiz kontrat ihlali (review H-9).
        if (!string.Equals(interval, "daily", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException(
                string.Format(localizer["InvalidInterval"], interval),
                field: nameof(interval));

        // F2.2-1 ([C-B-AssetService-5/7]): cache key'e interval suffix ekle. Şu an
        // yalnızca "daily" destekleniyor; weekly/monthly eklenirse aynı (symbol, from, to)
        // çiftinin farklı interval'larda farklı response'u olur → cache key ayrımı şart.
        var normalizedInterval = interval.ToLowerInvariant();
        var cacheKey = $"prices:{symbol.ToUpperInvariant()}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{normalizedInterval}";

        var cached = await cache.TryGetAsync<List<PricePoint>>(cacheKey, ct);
        if (cached is not null) return cached;

        var points = await repository.GetPriceRangeAsync(symbol.ToUpperInvariant(), from, to, ct);

        await cache.TrySetAsync(cacheKey, points, TimeSpan.FromHours(1), ct);

        // CLAUDE.md: "LogDebug yalnızca Development ortamında, detay bilgi".
        // Production minimum log seviyesi Information olduğu için bu zaten no-op; ama
        // IsEnabled check'i ile string interpolation/boxing maliyeti tamamen sıfırlanır.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("GetPriceRange dönüyor: {Symbol} {From}-{To} ({Count} nokta)",
                symbol, from, to, points.Count);

        return points;
    }
}
