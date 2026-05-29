using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Options;
using Saydin.Api.Services;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.IntegrationTests;

/// <summary>
/// F1.9-7: DailyLimitGuard'ın atomik check-then-INCR Lua script'i GERÇEK Redis'e karşı
/// doğrulanır (önceden yalnız mock'lanmıştı). Limit aşımı server-side atomik reddedilmeli.
/// </summary>
[Collection(RedisCollection.Name)]
public class DailyLimitGuardIntegrationTests(RedisFixture redis)
{
    [SkippableFact]
    public async Task TryAcquireAsync_RealRedis_EnforcesLimitAtomically()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);

        // Benzersiz prefix → diğer testler/üretim sayaçlarıyla çakışmaz.
        var prefix   = $"usage:itest:{Guid.NewGuid():N}:";
        var deviceId = "itest-device";
        var options  = Microsoft.Extensions.Options.Options.Create(new PlanOptions
        {
            Free = new TierOptions { DailyCalculationLimit = 2 },
        });
        var guard = new DailyLimitGuard(
            redis.Multiplexer!, options, TimeProvider.System, NullLogger<DailyLimitGuard>.Instance);

        try
        {
            // Limit=2: ilk iki acquire geçer, üçüncü gerçek Lua script ile reddedilir.
            await guard.TryAcquireAsync(null, deviceId, prefix);
            await guard.TryAcquireAsync(null, deviceId, prefix);

            var third = () => guard.TryAcquireAsync(null, deviceId, prefix);
            await third.Should().ThrowAsync<DailyLimitExceededException>();
        }
        finally
        {
            // Sayaç key'ini temizle (TTL gece yarısı; testler arası birikmesin).
            var key = DailyLimitGuard.BuildUsageKey(null, deviceId, prefix);
            await redis.Multiplexer!.GetDatabase().KeyDeleteAsync(key);
        }
    }
}
