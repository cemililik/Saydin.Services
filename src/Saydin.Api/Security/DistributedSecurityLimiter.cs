using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Saydin.DatabaseSecurity;
using StackExchange.Redis;

namespace Saydin.Api.Security;

public interface IDistributedSecurityLimiter
{
    ValueTask<SecurityLimiterDecision> TryAcquireNetworkAsync(
        IPAddress clientAddress,
        CancellationToken cancellationToken = default);

    ValueTask<SecurityLimiterDecision> TryAcquirePrincipalAsync(
        Guid principalId,
        CancellationToken cancellationToken = default);

    ValueTask<SecurityLimiterDecision> TryAcquireRegistrationAsync(
        IPAddress clientAddress,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseRegistrationAsync(IPAddress clientAddress);

    ValueTask<SecurityLimiterDecision> TryAcquireCalculationNetworkAsync(
        IPAddress clientAddress,
        CancellationToken cancellationToken = default);
}

public sealed class DistributedSecurityLimiter(
    IConnectionMultiplexer redis,
    IOptions<DistributedSecurityLimiterOptions> options,
    SecurityLimiterPseudonymizer pseudonymizer,
    ILogger<DistributedSecurityLimiter> logger)
    : IDistributedSecurityLimiter
{
    private const int HourMilliseconds = 60 * 60 * 1000;
    private const int DayMilliseconds = 24 * HourMilliseconds;

    private const string Script = """
        local now = redis.call('TIME')
        local now_ms = tonumber(now[1]) * 1000 + math.floor(tonumber(now[2]) / 1000)
        local retry_ms = 0

        for i = 1, #KEYS do
          local argument = ((i - 1) * 2) + 1
          local window_ms = tonumber(ARGV[argument])
          local limit = tonumber(ARGV[argument + 1])
          local window = math.floor(now_ms / window_ms)
          local stored_window = tonumber(redis.call('HGET', KEYS[i], 'window') or '-1')
          local count = 0
          if stored_window == window then
            count = tonumber(redis.call('HGET', KEYS[i], 'count') or '0')
          end
          if count >= limit then
            local candidate_retry = ((window + 1) * window_ms) - now_ms
            if candidate_retry > retry_ms then retry_ms = candidate_retry end
          end
        end
        if retry_ms > 0 then return {0, retry_ms} end

        for i = 1, #KEYS do
          local argument = ((i - 1) * 2) + 1
          local window_ms = tonumber(ARGV[argument])
          local window = math.floor(now_ms / window_ms)
          local stored_window = tonumber(redis.call('HGET', KEYS[i], 'window') or '-1')
          local count = 0
          if stored_window == window then
            count = tonumber(redis.call('HGET', KEYS[i], 'count') or '0')
          end
          redis.call('HSET', KEYS[i], 'window', window, 'count', count + 1)
          redis.call('PEXPIRE', KEYS[i], window_ms * 2)
        end
        return {1, 0}
        """;

    private const string ReleaseScript = """
        local now = redis.call('TIME')
        local now_ms = tonumber(now[1]) * 1000 + math.floor(tonumber(now[2]) / 1000)
        for i = 1, #KEYS do
          local window_ms = tonumber(ARGV[i])
          local window = math.floor(now_ms / window_ms)
          local stored_window = tonumber(redis.call('HGET', KEYS[i], 'window') or '-1')
          if stored_window == window then
            local count = tonumber(redis.call('HGET', KEYS[i], 'count') or '0')
            if count <= 1 then
              redis.call('DEL', KEYS[i])
            else
              redis.call('HSET', KEYS[i], 'count', count - 1)
            end
          end
        end
        return 1
        """;

    private readonly DistributedSecurityLimiterOptions _options = options.Value;

    public async ValueTask<SecurityLimiterDecision> TryAcquireNetworkAsync(
        IPAddress clientAddress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled) return SecurityLimiterDecision.Allowed;

        if (!TryNormalizeAddress(clientAddress, out var exactBytes, out var networkBytes))
            return SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.InvalidSubject);

        try
        {
            var exactDigest = pseudonymizer.Hash("exact-ip", exactBytes);
            var networkDigest = pseudonymizer.Hash("network", networkBytes);
            return await TryAcquireBucketsAsync(
                [BuildKey("exact", exactDigest), BuildKey("network", networkDigest)],
                [_options.ExactIpLimit, _options.NetworkLimit],
                [_options.WindowSeconds * 1000L, _options.WindowSeconds * 1000L],
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exactBytes);
            CryptographicOperations.ZeroMemory(networkBytes);
        }
    }

    public async ValueTask<SecurityLimiterDecision> TryAcquirePrincipalAsync(
        Guid principalId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled) return SecurityLimiterDecision.Allowed;
        if (principalId == Guid.Empty)
            return SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.InvalidSubject);

        Span<byte> principalBytes = stackalloc byte[16];
        if (!principalId.TryWriteBytes(principalBytes, bigEndian: true, out var bytesWritten) ||
            bytesWritten != principalBytes.Length)
            return SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.InvalidSubject);
        var digest = pseudonymizer.Hash("principal", principalBytes);
        CryptographicOperations.ZeroMemory(principalBytes);
        return await TryAcquireBucketsAsync(
            [BuildKey("principal", digest)],
            [_options.PrincipalLimit],
            [_options.WindowSeconds * 1000L],
            cancellationToken);
    }

    public async ValueTask<SecurityLimiterDecision> TryAcquireRegistrationAsync(
        IPAddress clientAddress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled) return SecurityLimiterDecision.Allowed;
        var isIpv4 = clientAddress.IsIPv4MappedToIPv6
            || clientAddress.AddressFamily == AddressFamily.InterNetwork;
        if (!TryNormalizeAddress(clientAddress, out var exactBytes, out var networkBytes))
            return SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.InvalidSubject);

        try
        {
            var exactDigest = pseudonymizer.Hash("registration-exact", exactBytes);
            var networkDigest = pseudonymizer.Hash("registration-network", networkBytes);
            if (isIpv4)
                return await TryAcquireBucketsAsync(
                    [
                        BuildKey("registration-v4-exact-hour", exactDigest),
                        BuildKey("registration-v4-network-hour", networkDigest),
                    ],
                    [
                        _options.RegistrationIpv4ExactHourlyLimit,
                        _options.RegistrationIpv4NetworkHourlyLimit,
                    ],
                    [HourMilliseconds, HourMilliseconds],
                    cancellationToken);

            return await TryAcquireBucketsAsync(
                [
                    BuildKey("registration-v6-exact-hour", exactDigest),
                    BuildKey("registration-v6-exact-day", exactDigest),
                    BuildKey("registration-v6-network-hour", networkDigest),
                    BuildKey("registration-v6-network-day", networkDigest),
                ],
                [
                    _options.RegistrationExactHourlyLimit,
                    _options.RegistrationExactDailyLimit,
                    _options.RegistrationNetworkHourlyLimit,
                    _options.RegistrationNetworkDailyLimit,
                ],
                [HourMilliseconds, DayMilliseconds, HourMilliseconds, DayMilliseconds],
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exactBytes);
            CryptographicOperations.ZeroMemory(networkBytes);
        }
    }

    public async ValueTask ReleaseRegistrationAsync(IPAddress clientAddress)
    {
        if (!_options.Enabled) return;
        var isIpv4 = clientAddress.IsIPv4MappedToIPv6
            || clientAddress.AddressFamily == AddressFamily.InterNetwork;
        if (!TryNormalizeAddress(clientAddress, out var exactBytes, out var networkBytes))
            return;

        try
        {
            var exactDigest = pseudonymizer.Hash("registration-exact", exactBytes);
            var networkDigest = pseudonymizer.Hash("registration-network", networkBytes);
            RedisKey[] keys;
            RedisValue[] windows;
            if (isIpv4)
            {
                keys =
                [
                    BuildKey("registration-v4-exact-hour", exactDigest),
                    BuildKey("registration-v4-network-hour", networkDigest),
                ];
                windows = [HourMilliseconds, HourMilliseconds];
            }
            else
            {
                keys =
                [
                    BuildKey("registration-v6-exact-hour", exactDigest),
                    BuildKey("registration-v6-exact-day", exactDigest),
                    BuildKey("registration-v6-network-hour", networkDigest),
                    BuildKey("registration-v6-network-day", networkDigest),
                ];
                windows = [HourMilliseconds, DayMilliseconds, HourMilliseconds, DayMilliseconds];
            }

            await redis.GetDatabase().ScriptEvaluateAsync(
                ReleaseScript, keys, windows).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            // Compensation is best effort and must never hide the original handler
            // failure. A missed release is fail-safe (stricter admission), and is
            // observable without logging the client address or Redis key.
            logger.LogError(exception,
                "Registration admission compensation failed: {Code}",
                "security_registration_compensation_failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exactBytes);
            CryptographicOperations.ZeroMemory(networkBytes);
        }
    }

    public async ValueTask<SecurityLimiterDecision> TryAcquireCalculationNetworkAsync(
        IPAddress clientAddress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled) return SecurityLimiterDecision.Allowed;
        var isIpv4 = clientAddress.IsIPv4MappedToIPv6
            || clientAddress.AddressFamily == AddressFamily.InterNetwork;
        if (!TryNormalizeAddress(clientAddress, out var exactBytes, out var networkBytes))
            return SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.InvalidSubject);

        try
        {
            // IPv4 public addresses and /24s are routinely shared by CGNAT. The
            // general minute bucket and authenticated principal bucket already
            // bound bursts; a shared daily IPv4 budget would create neighbour DoS.
            if (isIpv4) return SecurityLimiterDecision.Allowed;
            var networkDigest = pseudonymizer.Hash("calculation-network", networkBytes);
            return await TryAcquireBucketsAsync(
                [BuildKey("calculation-v6-network-day", networkDigest)],
                [_options.CalculationNetworkDailyLimit],
                [DayMilliseconds],
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exactBytes);
            CryptographicOperations.ZeroMemory(networkBytes);
        }
    }

    private async ValueTask<SecurityLimiterDecision> TryAcquireBucketsAsync(
        RedisKey[] keys,
        int[] limits,
        long[] windowsMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            if (keys.Length == 0 || keys.Length != limits.Length
                || keys.Length != windowsMilliseconds.Length)
                return SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.InvalidSubject);
            var values = new RedisValue[checked(limits.Length * 2)];
            for (var index = 0; index < limits.Length; index++)
            {
                values[index * 2] = windowsMilliseconds[index];
                values[index * 2 + 1] = limits[index];
            }

            var result = await redis.GetDatabase().ScriptEvaluateAsync(
                Script, keys, values).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var parts = (RedisResult[]?)result;
            if (parts is null || parts.Length != 2 ||
                !long.TryParse(parts[0].ToString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var allowed) ||
                !long.TryParse(parts[1].ToString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var retryMilliseconds) ||
                retryMilliseconds < 0 || retryMilliseconds > windowsMilliseconds.Max() ||
                allowed == 1 && retryMilliseconds != 0 ||
                allowed == 0 && retryMilliseconds == 0)
            {
                logger.LogWarning("Distributed security limiter malformed reply: {Code}",
                    "security_limiter_malformed_reply");
                return SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.MalformedReply);
            }

            return allowed switch
            {
                1 => SecurityLimiterDecision.Allowed,
                0 => SecurityLimiterDecision.Limited(TimeSpan.FromMilliseconds(
                    retryMilliseconds)),
                _ => SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.MalformedReply),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Distributed security limiter Redis operation failed: {Code}",
                "security_limiter_redis_failure");
            return SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.RedisFailure);
        }
    }

    private RedisKey BuildKey(string bucket, string digest) =>
        $"{_options.RedisKeyPrefix}{{security-rate-v1}}:{bucket}:{digest}";

    internal static bool TryNormalizeAddress(
        IPAddress address,
        out byte[] exact,
        out byte[] network)
    {
        exact = [];
        network = [];
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) ||
            IPAddress.None.Equals(address) || IPAddress.IPv6None.Equals(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            exact = address.GetAddressBytes();
            network = exact.ToArray();
            network[3] = 0;
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            exact = address.GetAddressBytes();
            network = exact.ToArray();
            Array.Clear(network, 8, 8);
            return true;
        }

        return false;
    }
}

public sealed class SecurityLimiterPseudonymizer : IDisposable
{
    private readonly byte[] _key;

    public SecurityLimiterPseudonymizer(IOptions<DistributedSecurityLimiterOptions> options)
    {
        _key = options.Value.Enabled
            ? Encoding.UTF8.GetBytes(SecureSecretFile.ReadPassword(options.Value.HmacKeyFile))
            : [];
    }

    internal SecurityLimiterPseudonymizer(ReadOnlySpan<byte> key)
    {
        if (key.Length < 24 || key.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(key));
        _key = key.ToArray();
    }

    internal string Hash(string domain, ReadOnlySpan<byte> value)
    {
        if (_key.Length == 0) throw new InvalidOperationException("security_limiter_disabled");
        var domainBytes = Encoding.ASCII.GetBytes(domain);
        var input = new byte[checked(2 + domainBytes.Length + value.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(input, checked((ushort)domainBytes.Length));
        domainBytes.CopyTo(input, 2);
        value.CopyTo(input.AsSpan(2 + domainBytes.Length));
        try
        {
            return Convert.ToHexStringLower(HMACSHA256.HashData(_key, input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(domainBytes);
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
