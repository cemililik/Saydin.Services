using System.Text.Json;
using StackExchange.Redis;

namespace Saydin.Api.Services;

/// <summary>
/// Tek noktada toplanmış Redis cache helper'ı: WhatIfCalculator, DcaCalculator ve
/// AssetService aynı try/catch + JSON pattern'ini paylaşır. Redis hatası kullanıcı
/// yoluna sızmaz (sadece warning + cache-miss davranışı).
/// </summary>
public interface IRedisCacheHelper
{
    Task<T?> TryGetAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task TrySetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);
}

public sealed class RedisCacheHelper(
    IConnectionMultiplexer redis,
    ILogger<RedisCacheHelper> logger) : IRedisCacheHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<T?> TryGetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var db = redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            if (!value.HasValue) return null;
            return JsonSerializer.Deserialize<T>(value.ToString(), JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis okuma hatası: {Key}", key);
            return null;
        }
    }

    public async Task TrySetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var db = redis.GetDatabase();
            await db.StringSetAsync(key, JsonSerializer.Serialize(value, JsonOptions), ttl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis yazma hatası: {Key}", key);
        }
    }
}
