using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Options;
using Saydin.Api.Services;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class DailyLimitGuardIntegrationTests(RedisFixture redis)
{
    [SkippableFact]
    public async Task Lease_RealRedis_IsAtomicIdempotentAndRetainedFor48Hours()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var prefix = $"usage:lease-itest:{Guid.NewGuid():N}:";
        var guard = CreateGuard(limit: 2);
        var leases = new List<QuotaLease>();

        try
        {
            var first = await guard.TryAcquireAsync(null, "device", prefix);
            var second = await guard.TryAcquireAsync(null, "device", prefix);
            leases.Add(first);
            leases.Add(second);
            first.Nonce.Should().NotBe(second.Nonce);

            var rejected = () => guard.TryAcquireAsync(null, "device", prefix);
            await rejected.Should().ThrowAsync<DailyLimitExceededException>();

            var database = redis.Multiplexer!.GetDatabase();
            (await database.HashGetAsync(first.RedisKey, "count")).Should().Be((RedisValue)2);
            (await database.KeyTimeToLiveAsync(first.RedisKey)).Should().BeGreaterThan(TimeSpan.FromHours(47));

            await guard.ReleaseAsync(first);
            await guard.ReleaseAsync(first); // same nonce cannot decrement twice
            (await database.HashGetAsync(first.RedisKey, "count")).Should().Be((RedisValue)1);

            var replacement = await guard.TryAcquireAsync(null, "device", prefix);
            leases.Add(replacement);
            (await database.HashGetAsync(first.RedisKey, "count")).Should().Be((RedisValue)2);
        }
        finally
        {
            foreach (var key in leases.Select(lease => (RedisKey)lease.RedisKey).Distinct())
                await redis.Multiplexer!.GetDatabase().KeyDeleteAsync(key);
        }
    }

    [SkippableFact]
    public async Task TwoGuardInstances_ShareOneAtomicCap()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var prefix = $"usage:replica-itest:{Guid.NewGuid():N}:";
        var firstGuard = CreateGuard(limit: 25);
        var secondGuard = CreateGuard(limit: 25);
        var leases = new List<QuotaLease>();

        try
        {
            var attempts = Enumerable.Range(0, 60).Select(async index =>
            {
                try
                {
                    var lease = await (index % 2 == 0 ? firstGuard : secondGuard)
                        .TryAcquireAsync(null, "shared-device", prefix);
                    lock (leases) leases.Add(lease);
                    return true;
                }
                catch (DailyLimitExceededException)
                {
                    return false;
                }
            });

            var results = await Task.WhenAll(attempts);
            results.Count(allowed => allowed).Should().Be(25);
            results.Count(allowed => !allowed).Should().Be(35);
        }
        finally
        {
            foreach (var key in leases.Select(lease => (RedisKey)lease.RedisKey).Distinct())
                await redis.Multiplexer!.GetDatabase().KeyDeleteAsync(key);
        }
    }

    [SkippableFact]
    public async Task AcquireScript_ReplayWithSameNonce_DoesNotIncrementAgain()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var prefix = $"usage:replay-itest:{Guid.NewGuid():N}:";
        var guard = CreateGuard(limit: 2);
        var lease = await guard.TryAcquireAsync(null, "device", prefix);
        var database = redis.Multiplexer!.GetDatabase();

        try
        {
            var time = (RedisResult[]?)await database.ExecuteAsync("TIME")
                ?? throw new InvalidOperationException("Redis TIME returned no values.");
            var day = long.Parse(time[0].ToString(), System.Globalization.CultureInfo.InvariantCulture) / 86400;
            var replay = (RedisResult[]?)await database.ScriptEvaluateAsync(
                DailyLimitGuard.AcquireScript,
                [lease.RedisKey],
                [day, 2, lease.Nonce, 172_800_000L])
                ?? throw new InvalidOperationException("Quota replay returned no values.");

            replay[0].ToString().Should().Be("1");
            (await database.HashGetAsync(lease.RedisKey, "count")).Should().Be((RedisValue)1);
        }
        finally
        {
            await database.KeyDeleteAsync(lease.RedisKey);
        }
    }

    private DailyLimitGuard CreateGuard(int limit) => new(
        redis.Multiplexer!,
        Microsoft.Extensions.Options.Options.Create(new PlanOptions
        {
            Free = new TierOptions { DailyCalculationLimit = limit },
        }),
        new FixedQuotaSubjectPseudonymizer(),
        TimeProvider.System,
        NullLogger<DailyLimitGuard>.Instance);

    private sealed class FixedQuotaSubjectPseudonymizer : IQuotaSubjectPseudonymizer
    {
        public string PseudonymizeQuotaSubject(string subject) =>
            "q1:" + Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(subject))).Substring(0, 32);
    }
}
