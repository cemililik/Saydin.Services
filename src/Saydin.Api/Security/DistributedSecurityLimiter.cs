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
}

public sealed class DistributedSecurityLimiter(
    IConnectionMultiplexer redis,
    IOptions<DistributedSecurityLimiterOptions> options,
    SecurityLimiterPseudonymizer pseudonymizer)
    : IDistributedSecurityLimiter
{
    private const string Script = """
        local now = redis.call('TIME')
        local now_ms = tonumber(now[1]) * 1000 + math.floor(tonumber(now[2]) / 1000)
        local window_ms = tonumber(ARGV[1])
        local window = math.floor(now_ms / window_ms)
        local retry_ms = ((window + 1) * window_ms) - now_ms

        for i = 1, #KEYS do
          local stored_window = tonumber(redis.call('HGET', KEYS[i], 'window') or '-1')
          local count = 0
          if stored_window == window then
            count = tonumber(redis.call('HGET', KEYS[i], 'count') or '0')
          end
          if count >= tonumber(ARGV[i + 1]) then
            return {0, retry_ms}
          end
        end

        for i = 1, #KEYS do
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

    private readonly DistributedSecurityLimiterOptions _options = options.Value;

    public async ValueTask<SecurityLimiterDecision> TryAcquireNetworkAsync(
        IPAddress clientAddress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled) return SecurityLimiterDecision.Allowed;

        if (!TryNormalizeAddress(clientAddress, out var exactBytes, out var networkBytes))
            return SecurityLimiterDecision.Unavailable;

        try
        {
            var exactDigest = pseudonymizer.Hash("exact-ip", exactBytes);
            var networkDigest = pseudonymizer.Hash("network", networkBytes);
            return await TryAcquireBucketsAsync(
                [BuildKey("exact", exactDigest), BuildKey("network", networkDigest)],
                [_options.ExactIpLimit, _options.NetworkLimit],
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
        if (principalId == Guid.Empty) return SecurityLimiterDecision.Unavailable;

        Span<byte> principalBytes = stackalloc byte[16];
        if (!principalId.TryWriteBytes(principalBytes, bigEndian: true, out var bytesWritten) ||
            bytesWritten != principalBytes.Length)
            return SecurityLimiterDecision.Unavailable;
        var digest = pseudonymizer.Hash("principal", principalBytes);
        CryptographicOperations.ZeroMemory(principalBytes);
        return await TryAcquireBucketsAsync(
            [BuildKey("principal", digest)],
            [_options.PrincipalLimit],
            cancellationToken);
    }

    private async ValueTask<SecurityLimiterDecision> TryAcquireBucketsAsync(
        RedisKey[] keys,
        int[] limits,
        CancellationToken cancellationToken)
    {
        try
        {
            var values = new RedisValue[limits.Length + 1];
            values[0] = checked(_options.WindowSeconds * 1000L);
            for (var index = 0; index < limits.Length; index++) values[index + 1] = limits[index];

            var result = await redis.GetDatabase().ScriptEvaluateAsync(
                Script, keys, values).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var parts = (RedisResult[]?)result;
            if (parts is null || parts.Length != 2 ||
                !long.TryParse(parts[0].ToString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var allowed) ||
                !long.TryParse(parts[1].ToString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var retryMilliseconds) ||
                retryMilliseconds < 0 || retryMilliseconds > _options.WindowSeconds * 1000L)
                return SecurityLimiterDecision.Unavailable;

            return allowed switch
            {
                1 => SecurityLimiterDecision.Allowed,
                0 => SecurityLimiterDecision.Limited(TimeSpan.FromMilliseconds(
                    Math.Max(1, retryMilliseconds))),
                _ => SecurityLimiterDecision.Unavailable,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SecurityLimiterDecision.Unavailable;
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
