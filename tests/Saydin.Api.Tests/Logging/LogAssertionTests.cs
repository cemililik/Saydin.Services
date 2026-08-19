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
/// assertion yaptığını gösterir. DailyLimitGuard Redis down yolunda fail-closed olur
/// ve yalnız kararlı hata kodunu loglar.
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
    public async Task DailyLimitGuard_RedisDown_FailsClosedAndLogsOnlyStableCode()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var db    = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.ExecuteAsync("TIME", Arg.Any<object[]>())
          .ThrowsAsync(new RedisConnectionException(
              ConnectionFailureType.UnableToConnect, "raw-secret-ip-sentinel"));

        var logger = new TestLogger<DailyLimitGuard>();
        var sut = new DailyLimitGuard(
            redis,
            Microsoft.Extensions.Options.Options.Create(new PlanOptions()),
            new FakeTimeProvider(),
            logger);

        var act = () => sut.CheckAsync(FreeUser, "raw-device-sentinel", "usage:whatif:");
        await act.Should().ThrowAsync<QuotaUnavailableException>();

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
        logger.Entries.Select(entry => entry.Message)
            .Should().OnlyContain(message =>
                message.Contains(QuotaUnavailableException.ErrorCode) &&
                !message.Contains("raw-secret-ip-sentinel", StringComparison.Ordinal) &&
                !message.Contains("raw-device-sentinel", StringComparison.Ordinal));
        logger.Entries.Should().OnlyContain(entry => entry.Exception == null);
    }
}
