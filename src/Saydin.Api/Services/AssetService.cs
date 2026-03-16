using System.Text.Json;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Services;

public sealed class AssetService(
    IPriceRepository repository,
    IConnectionMultiplexer redis,
    ILogger<AssetService> logger) : IAssetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct)
    {
        // Signature = aktif asset sayısı. 5 dakikada bir DB'den taze okunur.
        // Yeni asset eklenince sayı değişir → yeni cache key oluşur → otomatik invalidasyon.
        const string sigKey = "assets:sig";

        var sig = await TryGetCachedAsync<string>(sigKey);
        if (sig is null)
        {
            var count = await repository.GetActiveAssetCountAsync(ct);
            sig = count.ToString();
            await TrySetCacheAsync(sigKey, sig, TimeSpan.FromMinutes(5));
        }

        var listKey = $"assets:list:{sig}";
        var cached = await TryGetCachedAsync<List<Asset>>(listKey);
        if (cached is not null) return cached;

        var assets = await repository.GetAllActiveAssetsAsync(ct);
        await TrySetCacheAsync(listKey, assets, TimeSpan.FromHours(6));

        return assets;
    }

    public async Task<IReadOnlyList<AssetResponse>> GetAllAssetInfoAsync(CancellationToken ct)
    {
        const string sigKey = "assets:sig";

        var sig = await TryGetCachedAsync<string>(sigKey);
        if (sig is null)
        {
            var count = await repository.GetActiveAssetCountAsync(ct);
            sig = count.ToString();
            await TrySetCacheAsync(sigKey, sig, TimeSpan.FromMinutes(5));
        }

        var listKey = $"assets:info:{sig}";
        var cached = await TryGetCachedAsync<List<AssetResponse>>(listKey);
        if (cached is not null) return cached;

        var rows = await repository.GetAllActiveAssetsWithDateRangesAsync(ct);
        var result = rows
            .Select(r => new AssetResponse(
                r.Asset.Symbol,
                r.Asset.DisplayName,
                r.Asset.Category,
                r.FirstDate,
                r.LastDate))
            .ToList();

        await TrySetCacheAsync(listKey, result, TimeSpan.FromHours(1));
        return result;
    }

    public async Task<PricePoint> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        var cacheKey = $"price:{symbol.ToUpperInvariant()}:{date:yyyy-MM-dd}";

        var cached = await TryGetCachedAsync<PricePoint>(cacheKey);
        if (cached is not null) return cached;

        var price = await repository.GetPriceAsync(symbol.ToUpperInvariant(), date, ct);

        if (price is null)
            throw new PriceNotFoundException(symbol, date);

        await TrySetCacheAsync(cacheKey, price, TimeSpan.FromHours(24));

        return price;
    }

    public async Task<DateOnly> GetLatestPriceDateAsync(string symbol, CancellationToken ct)
    {
        var cacheKey = $"latest-date:{symbol.ToUpperInvariant()}";

        var cached = await TryGetCachedAsync<string>(cacheKey);
        if (cached is not null && DateOnly.TryParse(cached, out var cachedDate))
            return cachedDate;

        var date = await repository.GetLatestPriceDateAsync(symbol.ToUpperInvariant(), ct)
            ?? throw new PriceNotFoundException(symbol, DateOnly.FromDateTime(DateTime.UtcNow));

        await TrySetCacheAsync(cacheKey, date.ToString("yyyy-MM-dd"), TimeSpan.FromHours(1));

        return date;
    }

    public async Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, string interval, CancellationToken ct)
    {
        var cacheKey = $"prices:{symbol.ToUpperInvariant()}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";

        var cached = await TryGetCachedAsync<List<PricePoint>>(cacheKey);
        if (cached is not null) return cached;

        var points = await repository.GetPriceRangeAsync(symbol.ToUpperInvariant(), from, to, ct);

        await TrySetCacheAsync(cacheKey, points, TimeSpan.FromHours(1));

        return points;
    }

    // ── Redis yardımcıları ────────────────────────────────────────────────────

    private async Task<T?> TryGetCachedAsync<T>(string key) where T : class
    {
        try
        {
            var db = redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            if (!value.HasValue) return null;

            logger.LogDebug("Cache hit: {Key}", key);
            return JsonSerializer.Deserialize<T>(value.ToString(), JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis okuma hatası: {Key}", key);
            return null;  // Redis çöktüyse DB'ye düş
        }
    }

    private async Task TrySetCacheAsync<T>(string key, T value, TimeSpan ttl)
    {
        try
        {
            var db = redis.GetDatabase();
            await db.StringSetAsync(key, JsonSerializer.Serialize(value, JsonOptions), ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis yazma hatası: {Key}", key);
            // Cache yazılamazsa hata fırlatma — sadece logla
        }
    }
}
