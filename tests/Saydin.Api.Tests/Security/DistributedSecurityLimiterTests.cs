using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Security;
using Saydin.Api.Tests.Helpers;
using StackExchange.Redis;

namespace Saydin.Api.Tests.Security;

public sealed class DistributedSecurityLimiterTests
{
    private static readonly byte[] TestKey =
        Encoding.UTF8.GetBytes("security-limiter-test-key-32-bytes!");

    [Fact]
    public void NormalizeAddress_UsesV4Slash24()
    {
        DistributedSecurityLimiter.TryNormalizeAddress(
            IPAddress.Parse("203.0.113.97"), out var exact, out var network).Should().BeTrue();

        exact.Should().Equal(203, 0, 113, 97);
        network.Should().Equal(203, 0, 113, 0);
    }

    [Fact]
    public void NormalizeAddress_UsesV6Slash64()
    {
        DistributedSecurityLimiter.TryNormalizeAddress(
            IPAddress.Parse("2001:db8:abcd:1234:ffff:eeee:dddd:cccc"),
            out var exact, out var network).Should().BeTrue();

        exact[..8].Should().Equal(network[..8]);
        network[8..].Should().OnlyContain(value => value == 0);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    public void NormalizeAddress_RejectsUnknownOrUnusableAddresses(string value)
    {
        DistributedSecurityLimiter.TryNormalizeAddress(
            IPAddress.Parse(value), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Pseudonyms_AreDeterministicDomainSeparatedAndCultureInvariant()
    {
        using var pseudonymizer = new SecurityLimiterPseudonymizer(TestKey);
        var input = IPAddress.Parse("203.0.113.97").GetAddressBytes();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var exact = pseudonymizer.Hash("exact-ip", input);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var repeated = pseudonymizer.Hash("exact-ip", input);
            var network = pseudonymizer.Hash("network", input);

            exact.Should().Be(repeated);
            exact.Should().NotBe(network);
            exact.Should().MatchRegex("^[0-9a-f]{64}$");
            exact.Should().NotContain("203");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0, 60, 300, 120, false)]
    [InlineData(3601, 60, 300, 120, false)]
    [InlineData(60, 0, 300, 120, false)]
    [InlineData(60, 60, 1_000_001, 120, false)]
    [InlineData(60, 60, 300, 120, true)]
    public void Options_AreBounded(
        int window,
        int exactLimit,
        int networkLimit,
        int principalLimit,
        bool expected)
    {
        var options = new DistributedSecurityLimiterOptions
        {
            WindowSeconds = window,
            ExactIpLimit = exactLimit,
            NetworkLimit = networkLimit,
            PrincipalLimit = principalLimit,
        };

        DistributedSecurityLimiterOptions.HasValidShape(options).Should().Be(expected);
    }

    [Fact]
    public void DisabledOptions_DoNotRequireSecretFile()
    {
        var validator = new DistributedSecurityLimiterOptionsValidator();
        var options = ValidOptions();
        options.Enabled = false;
        options.HmacKeyFile = string.Empty;

        validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void EnabledOptions_RejectRelativeSecretFileWithStableFailure()
    {
        var validator = new DistributedSecurityLimiterOptionsValidator();
        var options = ValidOptions();
        options.HmacKeyFile = "relative-secret";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle("security_limiter_secret_invalid");
    }

    [Fact]
    public async Task InvalidOptions_FailDuringHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{DistributedSecurityLimiterOptions.SectionName}:Enabled"] = "true",
            [$"{DistributedSecurityLimiterOptions.SectionName}:HmacKeyFile"] = "relative-secret",
        });
        builder.Services.AddDistributedSecurityLimiter(builder.Configuration);
        using var host = builder.Build();

        var action = () => host.StartAsync();

        await action.Should().ThrowAsync<OptionsValidationException>()
            .Where(exception => exception.Failures.Contains("security_limiter_secret_invalid"));
    }

    [Fact]
    public async Task Limiter_UsesOnlyPseudonymizedKeys()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create([(RedisValue)1, (RedisValue)0]));
        using var pseudonymizer = new SecurityLimiterPseudonymizer(TestKey);
        var limiter = new DistributedSecurityLimiter(
            redis, Microsoft.Extensions.Options.Options.Create(ValidOptions()), pseudonymizer);

        var principalId = Guid.Parse("f00dbabe-0000-4000-8000-000000000001");
        var networkDecision = await limiter.TryAcquireNetworkAsync(
            IPAddress.Parse("203.0.113.97"));
        var principalDecision = await limiter.TryAcquirePrincipalAsync(principalId);

        networkDecision.Outcome.Should().Be(SecurityLimiterOutcome.Allowed);
        principalDecision.Outcome.Should().Be(SecurityLimiterOutcome.Allowed);
        await database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]?>(keys => keys != null && keys.Length == 2 && keys.All(key =>
                !key.ToString().Contains("203.0.113.97", StringComparison.Ordinal) &&
                !key.ToString().Contains("f00dbabe", StringComparison.Ordinal))),
            Arg.Any<RedisValue[]?>(),
            Arg.Any<CommandFlags>());
        await database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]?>(keys => keys != null && keys.Length == 1 && keys.All(key =>
                !key.ToString().Contains("203.0.113.97", StringComparison.Ordinal) &&
                !key.ToString().Contains("f00dbabe", StringComparison.Ordinal))),
            Arg.Any<RedisValue[]?>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Limiter_RedisFailure_ReturnsUnavailableWithoutThrowing()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "raw-ip-sentinel"));
        using var pseudonymizer = new SecurityLimiterPseudonymizer(TestKey);
        var limiter = new DistributedSecurityLimiter(
            redis, Microsoft.Extensions.Options.Options.Create(ValidOptions()), pseudonymizer);

        var decision = await limiter.TryAcquireNetworkAsync(IPAddress.Loopback);

        decision.Should().Be(SecurityLimiterDecision.Unavailable);
    }

    [Fact]
    public async Task Limiter_PreservesCancellation()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        using var pseudonymizer = new SecurityLimiterPseudonymizer(TestKey);
        var limiter = new DistributedSecurityLimiter(
            redis, Microsoft.Extensions.Options.Options.Create(ValidOptions()), pseudonymizer);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var action = async () => await limiter.TryAcquireNetworkAsync(
            IPAddress.Loopback, source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Limiter_CancellationInterruptsInFlightRedisWait()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var pending = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(pending.Task);
        using var pseudonymizer = new SecurityLimiterPseudonymizer(TestKey);
        var limiter = new DistributedSecurityLimiter(
            redis, Microsoft.Extensions.Options.Options.Create(ValidOptions()), pseudonymizer);
        using var source = new CancellationTokenSource();

        var operation = limiter.TryAcquireNetworkAsync(
            IPAddress.Loopback, source.Token).AsTask();
        await source.CancelAsync();
        var action = async () => await operation;

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Middleware_UnknownAddress_ReturnsStable503AndDoesNotInvokeNext()
    {
        var context = NewContext(address: null);
        var logger = new TestLogger<DistributedSecurityLimiterMiddleware>();
        var middleware = new DistributedSecurityLimiterMiddleware(
            new StubLimiter(SecurityLimiterDecision.Allowed),
            Microsoft.Extensions.Options.Options.Create(ValidOptions()), logger);
        var invoked = false;

        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        invoked.Should().BeFalse();
        context.Response.StatusCode.Should().Be(503);
        (await ReadCodeAsync(context)).Should().Be("security_limiter_unavailable");
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.9, definitely-not-an-ip")]
    [InlineData("198.51.100.1")]
    public async Task Middleware_UnconsumedForwardedAddress_Returns503(string forwardedFor)
    {
        var context = NewContext(IPAddress.Parse("192.0.2.10"));
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        var middleware = new DistributedSecurityLimiterMiddleware(
            new StubLimiter(SecurityLimiterDecision.Allowed),
            Microsoft.Extensions.Options.Options.Create(ValidOptions()),
            NullLogger<DistributedSecurityLimiterMiddleware>.Instance);
        var invoked = false;

        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        invoked.Should().BeFalse();
        context.Response.StatusCode.Should().Be(503);
        (await ReadCodeAsync(context)).Should().Be("security_limiter_unavailable");
    }

    [Fact]
    public async Task Middleware_LimiterUnavailable_ReturnsStable503()
    {
        var context = NewContext(IPAddress.Parse("192.0.2.10"));
        var middleware = new DistributedSecurityLimiterMiddleware(
            new StubLimiter(SecurityLimiterDecision.Unavailable),
            Microsoft.Extensions.Options.Options.Create(ValidOptions()),
            NullLogger<DistributedSecurityLimiterMiddleware>.Instance);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        context.Response.StatusCode.Should().Be(503);
        (await ReadCodeAsync(context)).Should().Be("security_limiter_unavailable");
    }

    [Fact]
    public async Task Middleware_DoesNotInferPrincipalBucketFromClaims()
    {
        var context = NewContext(IPAddress.Parse("203.0.113.97"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "raw-principal-sentinel")], "test"));
        var logger = new TestLogger<DistributedSecurityLimiterMiddleware>();
        var middleware = new DistributedSecurityLimiterMiddleware(
            new StubLimiter(SecurityLimiterDecision.Allowed),
            Microsoft.Extensions.Options.Options.Create(ValidOptions()), logger);

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        invoked.Should().BeTrue();
        context.Response.StatusCode.Should().Be(200);
        var messages = logger.Entries.Select(entry => entry.Message);
        messages.Should().NotContain(message =>
            message.Contains("203.0.113.97", StringComparison.Ordinal));
        messages.Should().NotContain(message =>
            message.Contains("raw-principal-sentinel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Middleware_Limit_ReturnsStable429AndBoundedRetryAfter()
    {
        var context = NewContext(IPAddress.Parse("203.0.113.97"));
        var middleware = new DistributedSecurityLimiterMiddleware(
            new StubLimiter(SecurityLimiterDecision.Limited(TimeSpan.FromMilliseconds(1100))),
            Microsoft.Extensions.Options.Options.Create(ValidOptions()),
            NullLogger<DistributedSecurityLimiterMiddleware>.Instance);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        context.Response.StatusCode.Should().Be(429);
        context.Response.Headers.RetryAfter.ToString().Should().Be("2");
        (await ReadCodeAsync(context)).Should().Be("security_rate_limited");
    }

    private static DistributedSecurityLimiterOptions ValidOptions() => new()
    {
        Enabled = true,
        WindowSeconds = 60,
        ExactIpLimit = 60,
        NetworkLimit = 300,
        PrincipalLimit = 120,
        RedisKeyPrefix = "security:test:",
    };

    private static DefaultHttpContext NewContext(IPAddress? address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string?> ReadCodeAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("code").GetString();
    }

    private sealed class StubLimiter(SecurityLimiterDecision decision)
        : IDistributedSecurityLimiter
    {
        public ValueTask<SecurityLimiterDecision> TryAcquireNetworkAsync(
            IPAddress clientAddress,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(decision);

        public ValueTask<SecurityLimiterDecision> TryAcquirePrincipalAsync(
            Guid principalId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(decision);
    }
}
