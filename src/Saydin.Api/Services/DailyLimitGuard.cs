using Microsoft.Extensions.Options;
using Saydin.Api.Options;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Services;

public sealed class DailyLimitGuard(
    IConnectionMultiplexer redis,
    IOptions<PlanOptions> options,
    ILogger<DailyLimitGuard> logger) : IDailyLimitGuard
{
    private const string PremiumTier = "premium";

    private (bool HasLimit, int Limit, string Key) GetLimitAndKey(
        User? user, string deviceId, string usageKeyPrefix, int? limitOverride, DateTime now)
    {
        // Premium kullanıcı override almıyorsa (yani caller bilinçli limit dayatmadıysa) sınırsız.
        // Karşılaştırma case-insensitive — tier "Premium" veya "PREMIUM" de gelebilir
        // (PlanOptions.GetTierOptions zaten OrdinalIgnoreCase ile çözüyor; aynı semantiği koru).
        if (limitOverride is null
            && string.Equals(user?.Tier, PremiumTier, StringComparison.OrdinalIgnoreCase))
            return (false, 0, string.Empty);

        var limit = limitOverride
            ?? options.Value.GetTierOptions(user?.Tier).DailyCalculationLimit;

        if (limit <= 0)
            return (false, 0, string.Empty);

        var key = BuildUsageKey(user, deviceId, usageKeyPrefix, now);
        return (true, limit, key);
    }

    public async Task CheckAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default)
    {
        // Tek nokta UtcNow okuması — key date'i ile TTL'in farklı dakikalardan
        // (gece yarısı geçişinde) çıkmasını engeller.
        var now = DateTime.UtcNow;
        var (hasLimit, limit, key) = GetLimitAndKey(user, deviceId, usageKeyPrefix, limitOverride, now);
        if (!hasLimit) return;

        try
        {
            ct.ThrowIfCancellationRequested();
            var db    = redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            var count = value.HasValue ? (long)value : 0;

            if (count >= limit)
                throw new DailyLimitExceededException(limit);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not DailyLimitExceededException)
        {
            logger.LogWarning(ex, "Daily limit Redis kontrolü başarısız, hesaplama devam ediyor");
        }
    }

    public Task IncrementAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default)
        => TryAcquireAsync(user, deviceId, usageKeyPrefix, limitOverride, ct);

    public async Task TryAcquireAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default)
    {
        // Aynı `now` değeri hem key date'i hem TTL hesabı için kullanılır;
        // BuildUsageKey'in bağımsız UtcNow okuması ile oluşan midnight race kapanır.
        var now = DateTime.UtcNow;
        var (hasLimit, limit, key) = GetLimitAndKey(user, deviceId, usageKeyPrefix, limitOverride, now);
        if (!hasLimit) return;

        var ttlMs = (long)(now.Date.AddDays(1) - now).TotalMilliseconds;
        try
        {
            ct.ThrowIfCancellationRequested();
            // F1.3-7: Check-then-INCR pattern — önceki INCR-then-DECR sayacı geçici
            // olarak limit'in üzerine şişiriyordu (cosmetic) ve telemetry / metric
            // okumalarında yanıltıcı oluyordu. Lua script atomic olduğu için race yok;
            // bu varyant niyeti daha net açıklıyor (review F1.3-7).
            // Dönüş: 1 = allow, 0 = reject (limit reached).
            const string script = """
                local current = tonumber(redis.call('GET', KEYS[1]) or '0')
                if current >= tonumber(ARGV[1]) then
                  return 0
                end
                redis.call('INCR', KEYS[1])
                redis.call('PEXPIRE', KEYS[1], ARGV[2])
                return 1
                """;
            var result = (long)await redis.GetDatabase()
                .ScriptEvaluateAsync(script, keys: [key], values: [limit, ttlMs]);

            if (result == 0)
                throw new DailyLimitExceededException(limit);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not DailyLimitExceededException)
        {
            logger.LogWarning(ex, "Daily limit increment başarısız: {Key}", key);
        }
    }

    public async Task ReleaseAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var (hasLimit, _, key) = GetLimitAndKey(user, deviceId, usageKeyPrefix, limitOverride, now);
        if (!hasLimit) return;

        try
        {
            ct.ThrowIfCancellationRequested();
            // Atomik DECR; sayaç 0'ın altına düşse bile günlük key TTL ile temizlenir.
            const string script = """
                local count = redis.call('GET', KEYS[1])
                if count and tonumber(count) > 0 then
                  return redis.call('DECR', KEYS[1])
                end
                return 0
                """;
            await redis.GetDatabase().ScriptEvaluateAsync(script, keys: [key]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Release best-effort: hata kullanıcının yoluna yansımaz, telemetry için log yeterli.
            logger.LogWarning(ex, "Daily limit release başarısız: {Key}", key);
        }
    }

    /// <summary>
    /// Usage key formatı: <c>{prefix}{userId|deviceId}:{yyyy-MM-dd}</c>.
    /// <paramref name="now"/> parametresi opsiyonel; verilmezse <see cref="DateTime.UtcNow"/>
    /// kullanılır (testler için kullanışlı). Production yollarında caller tek bir
    /// timestamp yakalayıp TTL hesabıyla aynı değeri buraya geçirir — gece yarısı
    /// race koşulu kapanır.
    /// </summary>
    internal static string BuildUsageKey(User? user, string deviceId, string prefix, DateTime? now = null)
    {
        var effective = now ?? DateTime.UtcNow;
        var userId  = user?.Id.ToString() ?? deviceId;
        var dateKey = effective.ToString("yyyy-MM-dd");
        return $"{prefix}{userId}:{dateKey}";
    }
}
