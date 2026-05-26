using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Options;
using Saydin.Api.Services;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Tests.Services;

public class DailyLimitGuardTests
{
    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase             _db    = Substitute.For<IDatabase>();
    private readonly DailyLimitGuard       _sut;

    private const string DeviceId = "test-device-001";
    private const string UsagePrefix = "usage:whatif:";

    private static readonly User FreeUser = new()
    {
        Id        = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        DeviceId  = "free-device",
        Tier      = "free",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly User PremiumUser = new()
    {
        Id        = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
        DeviceId  = "premium-device",
        Tier      = "premium",
        CreatedAt = DateTimeOffset.UtcNow
    };

    public DailyLimitGuardTests()
    {
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);

        var options = Microsoft.Extensions.Options.Options.Create(new PlanOptions());
        _sut = new DailyLimitGuard(_redis, options, NullLogger<DailyLimitGuard>.Instance);
    }

    // ── CheckAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_PremiumUser_SkipsRedisCheck()
    {
        await _sut.CheckAsync(PremiumUser, PremiumUser.DeviceId!, UsagePrefix);

        await _db.DidNotReceive()
            .StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task CheckAsync_FreeUserUnderLimit_DoesNotThrow()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns((RedisValue)5);

        var act = () => _sut.CheckAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckAsync_FreeUserAtLimit_ThrowsDailyLimitExceededException()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns((RedisValue)20);

        var act = () => _sut.CheckAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await act.Should().ThrowAsync<DailyLimitExceededException>()
                 .Where(ex => ex.Limit == 20);
    }

    [Fact]
    public async Task CheckAsync_FreeUserOverLimit_ThrowsDailyLimitExceededException()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns((RedisValue)25);

        var act = () => _sut.CheckAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await act.Should().ThrowAsync<DailyLimitExceededException>();
    }

    [Fact]
    public async Task CheckAsync_NoCachedValue_TreatsAsZero()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns(RedisValue.Null);

        var act = () => _sut.CheckAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckAsync_RedisDown_FailsOpenAndDoesNotThrow()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test"));

        var act = () => _sut.CheckAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckAsync_NullUser_UsesDeviceIdForKey()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns(RedisValue.Null);

        await _sut.CheckAsync(null, DeviceId, UsagePrefix);

        await _db.Received(1).StringGetAsync(
            Arg.Is<RedisKey>(k => k.ToString().Contains(DeviceId)),
            Arg.Any<CommandFlags>());
    }

    // ── IncrementAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task IncrementAsync_PremiumUser_SkipsRedisCall()
    {
        await _sut.IncrementAsync(PremiumUser, PremiumUser.DeviceId!, UsagePrefix);

        await _db.DidNotReceive()
            .ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task IncrementAsync_FreeUserUnderLimit_DoesNotThrow()
    {
        _db.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
           .Returns(RedisResult.Create((RedisValue)1));

        var act = () => _sut.IncrementAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IncrementAsync_FreeUserOverLimit_ThrowsDailyLimitExceededException()
    {
        _db.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
           .Returns(RedisResult.Create((RedisValue)(-1)));

        var act = () => _sut.IncrementAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await act.Should().ThrowAsync<DailyLimitExceededException>();
    }

    [Fact]
    public async Task IncrementAsync_RedisDown_FailsOpenAndDoesNotThrow()
    {
        _db.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
           .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test"));

        var act = () => _sut.IncrementAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await act.Should().NotThrowAsync();
    }

    // ── UnlimitedTier (limit = 0) ─────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_UnlimitedTier_SkipsRedisCheck()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PlanOptions
        {
            Free = new TierOptions { DailyCalculationLimit = 0 }
        });
        var sut = new DailyLimitGuard(_redis, options, NullLogger<DailyLimitGuard>.Instance);

        await sut.CheckAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await _db.DidNotReceive()
            .StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task IncrementAsync_UnlimitedTier_SkipsRedisCall()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PlanOptions
        {
            Free = new TierOptions { DailyCalculationLimit = 0 }
        });
        var sut = new DailyLimitGuard(_redis, options, NullLogger<DailyLimitGuard>.Instance);

        await sut.IncrementAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix);

        await _db.DidNotReceive()
            .ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    // ── limitOverride (asset endpoint kotası, tier-bağımsız) ──────────────

    [Fact]
    public async Task CheckAsync_LimitOverride_BypassesPremiumExemption()
    {
        // limitOverride verildiğinde premium muafiyeti devre dışı kalır —
        // /v1/assets/* asset query kotası bu mekanizmaya dayanır.
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns((RedisValue)10);

        await _sut.CheckAsync(PremiumUser, PremiumUser.DeviceId!, UsagePrefix,
            limitOverride: 500);

        await _db.Received(1).StringGetAsync(
            Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task CheckAsync_LimitOverride_OverridesTierLimit_NotThrownBelowOverride()
    {
        // FreeUser tier limit=20 olmasına rağmen, override=500 ile 30'dan throw olmamalı.
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns((RedisValue)30);

        var act = () => _sut.CheckAsync(FreeUser, FreeUser.DeviceId!, UsagePrefix,
            limitOverride: 500);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckAsync_LimitOverride_AtOverrideLimit_Throws()
    {
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns((RedisValue)500);

        var act = () => _sut.CheckAsync(null, DeviceId, UsagePrefix,
            limitOverride: 500);

        await act.Should().ThrowAsync<DailyLimitExceededException>()
                 .Where(ex => ex.Limit == 500);
    }

    [Fact]
    public async Task IncrementAsync_LimitOverride_PassesOverrideToLuaScript()
    {
        _db.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
           .Returns(RedisResult.Create((RedisValue)1));

        await _sut.IncrementAsync(null, DeviceId, UsagePrefix, limitOverride: 500);

        // Lua script'in ARGV[1]=ttlMs, ARGV[2]=limit. Override 500 olarak geçirilmiş olmalı.
        await _db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]?>(),
            Arg.Is<RedisValue[]?>(v => v != null && (long)v[1] == 500L),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task IncrementAsync_LimitOverride_BypassesPremiumExemption()
    {
        _db.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
           .Returns(RedisResult.Create((RedisValue)1));

        await _sut.IncrementAsync(PremiumUser, PremiumUser.DeviceId!, UsagePrefix,
            limitOverride: 500);

        await _db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]?>(),
            Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    // ── BuildUsageKey ─────────────────────────────────────────────────────

    [Fact]
    public void BuildUsageKey_WithUser_UsesUserId()
    {
        var key = DailyLimitGuard.BuildUsageKey(FreeUser, "some-device", UsagePrefix);

        key.Should().StartWith(UsagePrefix);
        key.Should().Contain(FreeUser.Id.ToString());
        key.Should().NotContain("some-device");
    }

    [Fact]
    public void BuildUsageKey_WithoutUser_UsesDeviceId()
    {
        var key = DailyLimitGuard.BuildUsageKey(null, DeviceId, UsagePrefix);

        key.Should().StartWith(UsagePrefix);
        key.Should().Contain(DeviceId);
    }

    [Fact]
    public void BuildUsageKey_ContainsCurrentDate()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var key = DailyLimitGuard.BuildUsageKey(FreeUser, "device", UsagePrefix);

        key.Should().EndWith(today);
    }
}
