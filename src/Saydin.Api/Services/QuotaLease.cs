namespace Saydin.Api.Services;

/// <summary>
/// An immutable receipt for one daily-quota reservation. A release is bound to
/// this exact Redis key and nonce; callers must not reconstruct either value.
/// </summary>
public sealed class QuotaLease
{
    private QuotaLease(string redisKey, string nonce, bool acquired, bool isNoop)
    {
        RedisKey = redisKey;
        Nonce = nonce;
        Acquired = acquired;
        IsNoop = isNoop;
    }

    public string RedisKey { get; }
    public string Nonce { get; }
    public bool Acquired { get; }
    public bool IsNoop { get; }

    internal static QuotaLease CreateAcquired(string redisKey, string nonce) =>
        new(redisKey, nonce, acquired: true, isNoop: false);

    internal static QuotaLease Noop { get; } =
        new(string.Empty, string.Empty, acquired: false, isNoop: true);
}
