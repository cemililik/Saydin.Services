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
        User? user, string deviceId, string usageKeyPrefix)
    {
        if (user?.Tier == PremiumTier)
            return (false, 0, string.Empty);

        var limit = options.Value.GetTierOptions(user?.Tier).DailyCalculationLimit;
        if (limit <= 0)
            return (false, 0, string.Empty);

        var key = BuildUsageKey(user, deviceId, usageKeyPrefix);
        return (true, limit, key);
    }

    public async Task CheckAsync(User? user, string deviceId, string usageKeyPrefix)
    {
        var (hasLimit, limit, key) = GetLimitAndKey(user, deviceId, usageKeyPrefix);
        if (!hasLimit) return;

        try
        {
            var db    = redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            var count = value.HasValue ? (long)value : 0;

            if (count >= limit)
                throw new DailyLimitExceededException(limit);
        }
        catch (Exception ex) when (ex is not DailyLimitExceededException)
        {
            logger.LogWarning(ex, "Daily limit Redis kontrolü başarısız, hesaplama devam ediyor");
        }
    }

    public async Task IncrementAsync(User? user, string deviceId, string usageKeyPrefix)
    {
        var (hasLimit, limit, key) = GetLimitAndKey(user, deviceId, usageKeyPrefix);
        if (!hasLimit) return;

        var ttlMs = (long)(DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalMilliseconds;
        try
        {
            const string script = """
                local count = redis.call('INCR', KEYS[1])
                if count == 1 then
                  redis.call('PEXPIRE', KEYS[1], ARGV[1])
                end
                if tonumber(count) > tonumber(ARGV[2]) then
                  redis.call('DECR', KEYS[1])
                  return -1
                end
                return count
                """;
            var result = (long)await redis.GetDatabase()
                .ScriptEvaluateAsync(script, keys: [key], values: [ttlMs, limit]);

            if (result == -1)
                throw new DailyLimitExceededException(limit);
        }
        catch (Exception ex) when (ex is not DailyLimitExceededException)
        {
            logger.LogWarning(ex, "Daily limit increment başarısız: {Key}", key);
        }
    }

    internal static string BuildUsageKey(User? user, string deviceId, string prefix)
    {
        var userId  = user?.Id.ToString() ?? deviceId;
        var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return $"{prefix}{userId}:{dateKey}";
    }
}
