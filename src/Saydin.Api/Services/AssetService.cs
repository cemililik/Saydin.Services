using System.Text.Json;
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
        const string cacheKey = "assets:list";

        var cached = await TryGetCachedAsync<List<Asset>>(cacheKey);
        if (cached is not null) return cached;

        var assets = await repository.GetAllActiveAssetsAsync(ct);

        await TrySetCacheAsync(cacheKey, assets, TimeSpan.FromHours(6));

        return assets;
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
