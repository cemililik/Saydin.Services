using FluentAssertions;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Options;
using Saydin.Api.Services;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Tests.Services;

public sealed class DailyLimitGuardTests
{
    private const string Prefix = "usage:test:";
    private const string QuotaPseudonym = "q1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const long UnixSeconds = 1_783_000_000;
    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly DailyLimitGuard _guard;

    private static readonly User FreeUser = new()
    {
        Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        DeviceId = "device-free",
        Tier = "free",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly User PremiumUser = new()
    {
        Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
        DeviceId = "device-premium",
        Tier = "premium",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    public DailyLimitGuardTests()
    {
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_database);
        _database.ExecuteAsync("TIME", Arg.Any<object[]>()).Returns(TimeResult(UnixSeconds));
        var pseudonymizer = Substitute.For<IQuotaSubjectPseudonymizer>();
        pseudonymizer.PseudonymizeQuotaSubject(Arg.Any<string>()).Returns(QuotaPseudonym);
        _guard = new DailyLimitGuard(
            _redis,
            Microsoft.Extensions.Options.Options.Create(new PlanOptions()),
            pseudonymizer,
            new FakeTimeProvider(),
            NullLogger<DailyLimitGuard>.Instance);
    }

    [Fact]
    public async Task UnlimitedUser_ReturnsNoop_WithoutRedis()
    {
        var lease = await _guard.TryAcquireAsync(PremiumUser, "ignored", Prefix);

        lease.IsNoop.Should().BeTrue();
        lease.Acquired.Should().BeFalse();
        lease.RedisKey.Should().BeEmpty();
        lease.Nonce.Should().BeEmpty();
        await _database.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<object[]>());
    }

    [Fact]
    public async Task Acquire_ReturnsExactKeyAnd128BitNonce()
    {
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(Pair(1, UnixSeconds / 86400));

        var lease = await _guard.TryAcquireAsync(FreeUser, "ignored", Prefix);

        lease.Acquired.Should().BeTrue();
        lease.IsNoop.Should().BeFalse();
        lease.RedisKey.Should().Be(DailyLimitGuard.BuildUsageKey(
            QuotaPseudonym, Prefix, UnixSeconds / 86400));
        lease.RedisKey.Should().NotContain(FreeUser.Id.ToString("N"));
        lease.Nonce.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task ConsecutiveAcquire_UsesDistinctNonces()
    {
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(Pair(1, UnixSeconds / 86400));

        var first = await _guard.TryAcquireAsync(FreeUser, "ignored", Prefix);
        var second = await _guard.TryAcquireAsync(FreeUser, "ignored", Prefix);

        first.Nonce.Should().NotBe(second.Nonce);
    }

    [Fact]
    public async Task AmbiguousAcquireResponse_ReplaysSameNonceOnce()
    {
        RedisValue[]? firstValues = null;
        RedisValue[]? secondValues = null;
        var call = 0;
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                call++;
                var values = (RedisValue[]?)callInfo[2];
                if (call == 1)
                {
                    firstValues = values?.ToArray();
                    throw new RedisConnectionException(
                        ConnectionFailureType.SocketFailure, "response-lost");
                }

                secondValues = values?.ToArray();
                return Pair(1, UnixSeconds / 86400);
            });

        var lease = await _guard.TryAcquireAsync(FreeUser, "ignored", Prefix);

        lease.Acquired.Should().BeTrue();
        call.Should().Be(2);
        firstValues.Should().NotBeNull();
        secondValues.Should().NotBeNull();
        firstValues![2].ToString().Should().Be(secondValues![2].ToString())
            .And.Be(lease.Nonce);
    }

    [Fact]
    public async Task Acquire_AtLimit_ThrowsDailyLimitExceeded()
    {
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(Pair(0, UnixSeconds / 86400));

        var action = () => _guard.TryAcquireAsync(FreeUser, "ignored", Prefix);

        await action.Should().ThrowAsync<DailyLimitExceededException>()
            .Where(exception => exception.Limit == 20);
    }

    [Fact]
    public async Task FiniteQuota_RedisFailure_IsFailClosed()
    {
        _database.ExecuteAsync("TIME", Arg.Any<object[]>())
            .ThrowsAsync(new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "raw-ip-sentinel"));

        var action = () => _guard.TryAcquireAsync(FreeUser, "raw-device-sentinel", Prefix);

        await action.Should().ThrowAsync<QuotaUnavailableException>()
            .Where(exception => exception.Message == "The quota service is temporarily unavailable.");
    }

    [Fact]
    public async Task RedisMidnightRace_RetriesWithServerDayAndCapturesNewKey()
    {
        var oldDay = UnixSeconds / 86400;
        var newDay = oldDay + 1;
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(Pair(-1, newDay), Pair(1, newDay));

        var lease = await _guard.TryAcquireAsync(FreeUser, "ignored", Prefix);

        lease.RedisKey.Should().Be(DailyLimitGuard.BuildUsageKey(
            QuotaPseudonym, Prefix, newDay));
        await _database.Received(2).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Acquire_UsesExact48HourRetention()
    {
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(Pair(1, UnixSeconds / 86400));

        await _guard.TryAcquireAsync(FreeUser, "ignored", Prefix);

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]?>(),
            Arg.Is<RedisValue[]?>(values => values != null && values.Length == 4 &&
                (long)values[3] == 172_800_000L),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Release_UsesCapturedKeyAndNonce()
    {
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(Pair(1, UnixSeconds / 86400), RedisResult.Create((RedisValue)1));
        var lease = await _guard.TryAcquireAsync(FreeUser, "ignored", Prefix);

        await _guard.ReleaseAsync(lease);

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]?>(keys => keys != null && keys.Length == 1 &&
                keys[0].ToString() == lease.RedisKey),
            Arg.Is<RedisValue[]?>(values => values != null && values.Length == 1 &&
                values[0].ToString() == lease.Nonce),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task NoopRelease_DoesNotUseRedis()
    {
        await _guard.ReleaseAsync(QuotaLease.Noop);

        await _database.DidNotReceive().ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Cancellation_IsPreserved()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var action = () => _guard.TryAcquireAsync(
            FreeUser, "ignored", Prefix, ct: source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cancellation_InterruptsInFlightRedisWait()
    {
        var pending = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _database.ExecuteAsync("TIME", Arg.Any<object[]>()).Returns(pending.Task);
        using var source = new CancellationTokenSource();

        var operation = _guard.TryAcquireAsync(
            FreeUser, "ignored", Prefix, ct: source.Token);
        await source.CancelAsync();
        var action = async () => await operation;

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void BuildUsageKey_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var utcDay = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds() / 86400;
            var key = DailyLimitGuard.BuildUsageKey(
                QuotaPseudonym, Prefix, utcDay);
            key.Should().Be($"usage:test:{QuotaPseudonym}:2026-08-19");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static RedisResult TimeResult(long seconds) =>
        RedisResult.Create([(RedisValue)seconds, (RedisValue)0]);

    private static RedisResult Pair(long status, long day) =>
        RedisResult.Create([(RedisValue)status, (RedisValue)day]);
}
