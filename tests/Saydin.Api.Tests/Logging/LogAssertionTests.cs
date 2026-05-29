using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Options;
using Saydin.Api.Services;
using Saydin.Api.Tests.Helpers;
using Saydin.Shared.Entities;
using StackExchange.Redis;

namespace Saydin.Api.Tests.Logging;

/// <summary>
/// F2.6-15 / F2.6-8: <see cref="TestLogger{T}"/> sink'inin gerçek bir senaryoda log
/// assertion yaptığını gösterir. Önceden DailyLimitGuard fail-open (Redis down) yolunda
/// warning loglar ama NullLogger ile bu doğrulanamıyordu (review C-F-25 / C-Çapraz-C).
/// </summary>
public class LogAssertionTests
{
    private static readonly User FreeUser = new()
    {
        Id        = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        DeviceId  = "free-device",
        Tier      = "free",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task DailyLimitGuard_RedisDown_FailsOpenAndLogsWarning()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var db    = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
          .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test"));

        var logger = new TestLogger<DailyLimitGuard>();
        var sut = new DailyLimitGuard(
            redis,
            Microsoft.Extensions.Options.Options.Create(new PlanOptions()),
            new FakeTimeProvider(),
            logger);

        // Fail-open: Redis erişilemez → exception kullanıcıya yansımaz.
        var act = () => sut.CheckAsync(FreeUser, FreeUser.DeviceId!, "usage:whatif:");
        await act.Should().NotThrowAsync();

        // ...ama telemetri için bir Warning loglanır (TestLogger ile doğrulanır).
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }
}
