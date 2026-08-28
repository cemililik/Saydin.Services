using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;
using Saydin.PriceIngestion.Extensions;
using Saydin.PriceIngestion.Tests.Adapters;

namespace Saydin.PriceIngestion.Tests;

public sealed class HttpResilienceExtensionsTests
{
    [Fact]
    public async Task OneLogicalFailure_IsOnePlusThreeAttempts()
    {
        var (client, handler, _) = Build(() => HttpStatusCode.ServiceUnavailable);

        using var response = await client.GetAsync("https://provider.test/data");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.CallCount.Should().Be(4);
    }

    [Fact]
    public async Task RetryWithoutHeader_WaitsBeforeIssuingNextAttempt()
    {
        var (client, handler, time) = Build(
            () => HttpStatusCode.ServiceUnavailable,
            retryDelayOverride: HttpResilienceExtensions.BaseRetryDelay);
        using var stop = new CancellationTokenSource();

        var fetch = client.GetAsync("https://provider.test/data", stop.Token);

        handler.CallCount.Should().Be(1);
        fetch.IsCompleted.Should().BeFalse();

        // Polly's exponential backoff is jittered, so the first delay has no guaranteed
        // lower bound — asserting one (e.g. "at least a second") is a coin flip that CI
        // eventually loses. The contract the pipeline really provides is that the delay
        // is scheduled on the injected TimeProvider: while the fake clock is frozen the
        // retry cannot be issued, however many scheduler turns pass.
        for (var turn = 0; turn < 50; turn++)
            await Task.Yield();
        handler.CallCount.Should().Be(1,
            "the retry is scheduled on the injected clock, never issued inline");

        await AdvanceUntilAsync(time, () => handler.CallCount >= 2);
        stop.Cancel();
        await FluentActions.Awaiting(() => fetch)
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SecondLogicalFailureOpens_ThirdHasZeroWire_ThenHalfOpenCloses()
    {
        var status = HttpStatusCode.ServiceUnavailable;
        var (client, handler, time) = Build(() => status);
        for (var logical = 0; logical < 2; logical++)
        {
            using var response = await client.GetAsync("https://provider.test/data");
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        handler.CallCount.Should().Be(8);

        var openCall = () => client.GetAsync("https://provider.test/data");
        await openCall.Should().ThrowAsync<BrokenCircuitException>();
        handler.CallCount.Should().Be(8);

        status = HttpStatusCode.OK;
        time.Advance(HttpResilienceExtensions.BreakDuration);
        using var probe = await client.GetAsync("https://provider.test/data");
        probe.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.CallCount.Should().Be(9);
        using var closed = await client.GetAsync("https://provider.test/data");
        closed.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.CallCount.Should().Be(10);
    }

    [Fact]
    public void RetryAfter_IsMaximumOfBackoffAndCappedHeader()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromMinutes(10));

        HttpResilienceExtensions.ResolveRetryDelay(
                response, TimeSpan.FromSeconds(8), DateTimeOffset.UnixEpoch)
            .Should().Be(HttpResilienceExtensions.MaxRetryAfter);

        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(1));
        HttpResilienceExtensions.ResolveRetryDelay(
                response, TimeSpan.FromSeconds(8), DateTimeOffset.UnixEpoch)
            .Should().Be(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void RetryAfterDate_UsesClockAndRemainsBounded()
    {
        var now = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            now.AddSeconds(20));

        HttpResilienceExtensions.ResolveRetryDelay(
                response, TimeSpan.FromSeconds(4), now)
            .Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void PipelineBudgetConstants_AreSingleThreeMinuteContract()
    {
        HttpResilienceExtensions.TotalRequestTimeout.Should().Be(TimeSpan.FromMinutes(3));
        HttpResilienceExtensions.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(30));
        HttpResilienceExtensions.MaxRetryAttempts.Should().Be(3);
        HttpResilienceExtensions.BaseRetryDelay.Should().BePositive();
    }

    private static (HttpClient Client, StubHttpMessageHandler Handler, FakeTimeProvider Time) Build(
        Func<HttpStatusCode> status,
        TimeSpan retryDelayOverride = default)
    {
        var time = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status()));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddSaydinResilience(time, retryDelayOverride);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("test"), handler, time);
    }

    /// <summary>
    /// Drives the fake clock forward until <paramref name="predicate"/> holds. Advancing
    /// in bounded steps (rather than once) removes the race where the pipeline registers
    /// its timer after a single <c>Advance</c> call and would then never become due. The
    /// step cap keeps the total advanced time far below the pipeline's 3 minute budget so
    /// a retry — not a total-timeout — is what the predicate observes.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> predicate)
    {
        var step = HttpResilienceExtensions.BaseRetryDelay * 4;
        for (var advance = 0; advance < 10 && !predicate(); advance++)
        {
            time.Advance(step);
            for (var turn = 0; turn < 50 && !predicate(); turn++)
                await Task.Yield();
        }

        if (!predicate())
            throw new TimeoutException("Retry was not issued after advancing the fake clock.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(1);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("Asynchronous retry condition was not reached.");
            await Task.Yield();
        }
    }
}
