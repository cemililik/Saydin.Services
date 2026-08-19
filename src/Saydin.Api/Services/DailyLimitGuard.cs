using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Saydin.Api.Options;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Services;

public sealed class DailyLimitGuard : IDailyLimitGuard
{
    private const string PremiumTier = "premium";
    private const long RetentionMilliseconds = 48L * 60 * 60 * 1000;
    private const int MaximumCalendarRaceRetries = 2;

    private const string CheckScript = """
        local now = redis.call('TIME')
        local day = math.floor(tonumber(now[1]) / 86400)
        if day ~= tonumber(ARGV[1]) then
          return {-1, day}
        end
        local count = tonumber(redis.call('HGET', KEYS[1], 'count') or '0')
        if count >= tonumber(ARGV[2]) then
          return {0, day}
        end
        return {1, day}
        """;

    internal const string AcquireScript = """
        local now = redis.call('TIME')
        local day = math.floor(tonumber(now[1]) / 86400)
        if day ~= tonumber(ARGV[1]) then
          return {-1, day}
        end
        local lease_field = 'lease:' .. ARGV[3]
        if redis.call('HEXISTS', KEYS[1], lease_field) == 1 then
          return {1, day}
        end
        local count = tonumber(redis.call('HGET', KEYS[1], 'count') or '0')
        if count >= tonumber(ARGV[2]) then
          return {0, day}
        end
        redis.call('HINCRBY', KEYS[1], 'count', 1)
        redis.call('HSET', KEYS[1], lease_field, '1')
        redis.call('PEXPIRE', KEYS[1], ARGV[4])
        return {1, day}
        """;

    private const string ReleaseScript = """
        local removed = redis.call('HDEL', KEYS[1], 'lease:' .. ARGV[1])
        if removed == 0 then
          return 0
        end
        local count = tonumber(redis.call('HGET', KEYS[1], 'count') or '0')
        if count > 0 then
          redis.call('HINCRBY', KEYS[1], 'count', -1)
        end
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly IOptions<PlanOptions> _options;
    private readonly ILogger<DailyLimitGuard> _logger;

    public DailyLimitGuard(
        IConnectionMultiplexer redis,
        IOptions<PlanOptions> options,
        TimeProvider timeProvider,
        ILogger<DailyLimitGuard> logger)
    {
        _redis = redis;
        _options = options;
        _logger = logger;
        _ = timeProvider; // Kept for source/DI compatibility; Redis TIME is authoritative.
    }

    public async Task CheckAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default)
    {
        var (hasLimit, limit) = GetLimit(user, limitOverride);
        if (!hasLimit) return;

        try
        {
            var database = _redis.GetDatabase();
            var day = await GetRedisUtcDayAsync(database, ct);
            for (var attempt = 0; attempt < MaximumCalendarRaceRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var key = BuildUsageKey(user, deviceId, usageKeyPrefix, day);
                var result = ParsePair(await database.ScriptEvaluateAsync(
                    CheckScript, [key], [day, limit]).WaitAsync(ct));
                if (result.Status == -1)
                {
                    day = result.Day;
                    continue;
                }

                if (result.Status == 0) throw new DailyLimitExceededException(limit);
                if (result.Status != 1) throw new QuotaUnavailableException();
                return;
            }

            throw new QuotaUnavailableException();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DailyLimitExceededException)
        {
            throw;
        }
        catch (QuotaUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning("Daily quota decision unavailable: {Code}", QuotaUnavailableException.ErrorCode);
            throw new QuotaUnavailableException();
        }
    }

    public async Task IncrementAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default)
    {
        _ = await TryAcquireAsync(user, deviceId, usageKeyPrefix, limitOverride, ct);
    }

    public async Task<QuotaLease> TryAcquireAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default)
    {
        var (hasLimit, limit) = GetLimit(user, limitOverride);
        if (!hasLimit) return QuotaLease.Noop;

        try
        {
            var database = _redis.GetDatabase();
            var day = await GetRedisUtcDayAsync(database, ct);
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

            for (var attempt = 0; attempt < MaximumCalendarRaceRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var key = BuildUsageKey(user, deviceId, usageKeyPrefix, day);
                var values = new RedisValue[] { day, limit, nonce, RetentionMilliseconds };
                RedisResult rawResult;
                try
                {
                    rawResult = await database.ScriptEvaluateAsync(AcquireScript, [key], values)
                        .WaitAsync(ct);
                }
                catch (Exception exception) when (IsAmbiguousRedisFailure(exception))
                {
                    // The script may have committed before its response was lost. Replaying
                    // the same nonce is safe: HEXISTS returns success without incrementing.
                    ct.ThrowIfCancellationRequested();
                    rawResult = await database.ScriptEvaluateAsync(AcquireScript, [key], values)
                        .WaitAsync(ct);
                }

                ct.ThrowIfCancellationRequested();
                var result = ParsePair(rawResult);
                if (result.Status == -1)
                {
                    day = result.Day;
                    continue;
                }

                if (result.Status == 0) throw new DailyLimitExceededException(limit);
                if (result.Status != 1) throw new QuotaUnavailableException();
                return QuotaLease.CreateAcquired(key, nonce);
            }

            throw new QuotaUnavailableException();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DailyLimitExceededException)
        {
            throw;
        }
        catch (QuotaUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            // Do not log Redis keys, device identifiers, users, nonces or exception text.
            _logger.LogWarning("Daily quota acquisition unavailable: {Code}", QuotaUnavailableException.ErrorCode);
            throw new QuotaUnavailableException();
        }
    }

    public async Task ReleaseAsync(QuotaLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.IsNoop) return;
        if (!lease.Acquired || string.IsNullOrEmpty(lease.RedisKey) ||
            lease.Nonce.Length != 32 || !lease.Nonce.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid quota lease.", nameof(lease));

        try
        {
            ct.ThrowIfCancellationRequested();
            _ = await _redis.GetDatabase().ScriptEvaluateAsync(
                ReleaseScript, [lease.RedisKey], [lease.Nonce]).WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning("Daily quota release unavailable: {Code}", QuotaUnavailableException.ErrorCode);
            throw new QuotaUnavailableException();
        }
    }

    internal static string BuildUsageKey(
        User? user,
        string deviceId,
        string prefix,
        DateTime now) =>
        BuildUsageKey(user, deviceId, prefix,
            checked(new DateTimeOffset(now.ToUniversalTime()).ToUnixTimeSeconds() / 86400));

    internal static string BuildUsageKey(User? user, string deviceId, string prefix, long utcDay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Length > 128 || prefix.Any(char.IsControl))
            throw new ArgumentException("Invalid quota key prefix.", nameof(prefix));

        var subject = user?.Id.ToString("D", CultureInfo.InvariantCulture) ?? deviceId;
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (subject.Length > 256 || subject.Any(char.IsControl))
            throw new ArgumentException("Invalid quota subject.", nameof(deviceId));

        var date = DateTimeOffset.FromUnixTimeSeconds(checked(utcDay * 86400))
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{prefix}{subject}:{date}";
    }

    private (bool HasLimit, int Limit) GetLimit(User? user, int? limitOverride)
    {
        if (limitOverride is null &&
            string.Equals(user?.Tier, PremiumTier, StringComparison.OrdinalIgnoreCase))
            return (false, 0);

        var limit = limitOverride ?? _options.Value.GetTierOptions(user?.Tier).DailyCalculationLimit;
        return limit <= 0 ? (false, 0) : (true, limit);
    }

    private static async Task<long> GetRedisUtcDayAsync(IDatabase database, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await database.ExecuteAsync("TIME").WaitAsync(ct);
        var parts = (RedisResult[]?)result;
        if (parts is null || parts.Length != 2 || !long.TryParse(parts[0].ToString(),
                NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            throw new QuotaUnavailableException();
        ct.ThrowIfCancellationRequested();
        return seconds / 86400;
    }

    private static (long Status, long Day) ParsePair(RedisResult result)
    {
        var parts = (RedisResult[]?)result;
        if (parts is null || parts.Length != 2 ||
            !long.TryParse(parts[0].ToString(), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var status) ||
            !long.TryParse(parts[1].ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var day))
            throw new QuotaUnavailableException();
        return (status, day);
    }

    private static bool IsAmbiguousRedisFailure(Exception exception) =>
        exception is RedisConnectionException or RedisTimeoutException;
}
