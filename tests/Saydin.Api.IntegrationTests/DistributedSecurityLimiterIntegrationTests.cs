using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Security;
using StackExchange.Redis;

namespace Saydin.Api.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class DistributedSecurityLimiterIntegrationTests(RedisFixture redis)
{
    [SkippableFact]
    public async Task TwoReplicas_ShareExactIpAndPrincipalCaps_WithoutRawIdentifiers()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var unique = Guid.NewGuid().ToString("N");
        var options = new DistributedSecurityLimiterOptions
        {
            Enabled = true,
            WindowSeconds = 60,
            ExactIpLimit = 2,
            NetworkLimit = 20,
            PrincipalLimit = 2,
            RedisKeyPrefix = $"securityitest{unique}:",
        };
        using var firstPseudonymizer = new SecurityLimiterPseudonymizer(
            Encoding.UTF8.GetBytes("security-limiter-integration-key-one"));
        using var secondPseudonymizer = new SecurityLimiterPseudonymizer(
            Encoding.UTF8.GetBytes("security-limiter-integration-key-one"));
        var first = new DistributedSecurityLimiter(
            redis.Multiplexer!, Microsoft.Extensions.Options.Options.Create(options), firstPseudonymizer);
        var second = new DistributedSecurityLimiter(
            redis.Multiplexer!, Microsoft.Extensions.Options.Options.Create(options), secondPseudonymizer);
        var address = IPAddress.Parse("203.0.113.97");
        var principalId = Guid.Parse("f00dbabe-0000-4000-8000-000000000001");

        try
        {
            (await first.TryAcquireNetworkAsync(address)).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
            (await second.TryAcquireNetworkAsync(address)).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
            var networkDenied = await first.TryAcquireNetworkAsync(address);
            networkDenied.Outcome.Should().Be(SecurityLimiterOutcome.Limited);
            networkDenied.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero)
                .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));

            // Credential resolution invokes this separate path. It must neither
            // consume nor depend on the already-exhausted IP/network buckets.
            (await first.TryAcquirePrincipalAsync(principalId)).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
            (await second.TryAcquirePrincipalAsync(principalId)).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
            (await first.TryAcquirePrincipalAsync(principalId)).Outcome
                .Should().Be(SecurityLimiterOutcome.Limited);

            var server = redis.Multiplexer!.GetServer(redis.Multiplexer.GetEndPoints()[0]);
            var keys = server.Keys(pattern: $"{options.RedisKeyPrefix}*").ToArray();
            keys.Should().HaveCount(3);
            keys.Select(key => key.ToString()).Should().OnlyContain(key =>
                !key.Contains("203.0.113.97", StringComparison.Ordinal) &&
                !key.Contains("f00dbabe", StringComparison.Ordinal) &&
                !key.Contains("security-limiter-integration-key-one", StringComparison.Ordinal));
        }
        finally
        {
            await DeleteKeysAsync(options.RedisKeyPrefix);
        }
    }

    [SkippableFact]
    public async Task DifferentAddressesInSameV4Slash24_ShareNetworkCap()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var unique = Guid.NewGuid().ToString("N");
        var options = new DistributedSecurityLimiterOptions
        {
            Enabled = true,
            WindowSeconds = 60,
            ExactIpLimit = 10,
            NetworkLimit = 2,
            PrincipalLimit = 10,
            RedisKeyPrefix = $"securityitest{unique}:",
        };
        using var pseudonymizer = new SecurityLimiterPseudonymizer(
            Encoding.UTF8.GetBytes("security-limiter-integration-key-two"));
        var limiter = new DistributedSecurityLimiter(
            redis.Multiplexer!, Microsoft.Extensions.Options.Options.Create(options), pseudonymizer);

        try
        {
            (await limiter.TryAcquireNetworkAsync(IPAddress.Parse("198.51.100.1"))).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
            (await limiter.TryAcquireNetworkAsync(IPAddress.Parse("198.51.100.2"))).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
            (await limiter.TryAcquireNetworkAsync(IPAddress.Parse("198.51.100.3"))).Outcome
                .Should().Be(SecurityLimiterOutcome.Limited);
        }
        finally
        {
            await DeleteKeysAsync(options.RedisKeyPrefix);
        }
    }

    [SkippableFact]
    public async Task DifferentAddressesInSameV6Slash64_ShareNetworkCap()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var unique = Guid.NewGuid().ToString("N");
        var options = new DistributedSecurityLimiterOptions
        {
            Enabled = true,
            WindowSeconds = 60,
            ExactIpLimit = 10,
            NetworkLimit = 2,
            PrincipalLimit = 10,
            RedisKeyPrefix = $"securityitest{unique}:",
        };
        using var pseudonymizer = new SecurityLimiterPseudonymizer(
            Encoding.UTF8.GetBytes("security-limiter-integration-key-v6x"));
        var limiter = new DistributedSecurityLimiter(
            redis.Multiplexer!, Microsoft.Extensions.Options.Options.Create(options), pseudonymizer);

        try
        {
            (await limiter.TryAcquireNetworkAsync(IPAddress.Parse("2001:db8:abcd:1::1"))).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
            (await limiter.TryAcquireNetworkAsync(IPAddress.Parse("2001:db8:abcd:1::2"))).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
            (await limiter.TryAcquireNetworkAsync(IPAddress.Parse("2001:db8:abcd:1::3"))).Outcome
                .Should().Be(SecurityLimiterOutcome.Limited);
            (await limiter.TryAcquireNetworkAsync(IPAddress.Parse("2001:db8:abcd:2::1"))).Outcome
                .Should().Be(SecurityLimiterOutcome.Allowed);
        }
        finally
        {
            await DeleteKeysAsync(options.RedisKeyPrefix);
        }
    }

    private async Task DeleteKeysAsync(string prefix)
    {
        var server = redis.Multiplexer!.GetServer(redis.Multiplexer.GetEndPoints()[0]);
        var keys = server.Keys(pattern: $"{prefix}*").ToArray();
        if (keys.Length > 0) await redis.Multiplexer.GetDatabase().KeyDeleteAsync(keys);
    }
}
