using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Saydin.PriceIngestion.Extensions;

internal static class HttpResilienceExtensions
{
    internal const int MaxRetryAttempts = 3;
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(2);
    // Must exceed the worst-case exhausted retry chain so stalled/timeout
    // requests remain in the breaker's sample window long enough to open it.
    internal static readonly TimeSpan SamplingDuration = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    public static IHttpClientBuilder AddSaydinResilience(
        this IHttpClientBuilder builder,
        TimeProvider? timeProvider = null,
        TimeSpan? retryDelayOverride = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var retryDelay = retryDelayOverride ?? BaseRetryDelay;
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.AddResilienceHandler("provider-authority-v1", pipeline =>
        {
            pipeline.TimeProvider = clock;

            // Strategies are outer-to-inner. One exhausted retry chain (including a
            // total-timeout exhaustion) is one breaker sample. The sampling window
            // is deliberately longer than two worst-case logical calls.
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                ShouldHandle = TransientHttpPredicate(),
                FailureRatio = 1.0,
                MinimumThroughput = 2,
                SamplingDuration = SamplingDuration,
                BreakDuration = BreakDuration,
            });
            // This is the single HTTP retry-chain budget and covers every attempt,
            // retry delay and response-header acquisition. Workers reuse the same
            // constant for body parsing/lease-renewal, which also bounds streams used
            // with ResponseHeadersRead. HttpClient.Timeout is deliberately disabled;
            // the two Polly timeouts below are the only HTTP time authorities.
            pipeline.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TotalRequestTimeout,
            });
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                ShouldHandle = TransientHttpPredicate(),
                MaxRetryAttempts = MaxRetryAttempts,
                Delay = retryDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldRetryAfterHeader = false,
                DelayGenerator = arguments =>
                {
                    var response = arguments.Outcome.Result;
                    if (response?.StatusCode != System.Net.HttpStatusCode.TooManyRequests
                        || response.Headers.RetryAfter is null)
                    {
                        // Let Polly apply its exponential backoff + jitter.
                        return new ValueTask<TimeSpan?>((TimeSpan?)null);
                    }

                    // A custom delay replaces Polly's generated delay. Preserve the
                    // exponential floor, add jitter, then honor the capped header only
                    // when it asks us to wait longer.
                    var backoff = JitteredExponentialDelay(retryDelay, arguments.AttemptNumber);
                    return new ValueTask<TimeSpan?>(ResolveRetryDelay(
                        response, backoff, clock.GetUtcNow()));
                },
            });
            pipeline.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = AttemptTimeout,
            });
        });
        return builder;
    }

    private static PredicateBuilder<HttpResponseMessage> TransientHttpPredicate() =>
        new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response =>
                response.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500);

    internal static TimeSpan ResolveRetryDelay(
        HttpResponseMessage response,
        TimeSpan backoff,
        DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;
        var requested = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - now : TimeSpan.Zero);
        var boundedHeader = requested <= TimeSpan.Zero
            ? TimeSpan.Zero
            : requested > MaxRetryAfter ? MaxRetryAfter : requested;
        return backoff >= boundedHeader ? backoff : boundedHeader;
    }

    private static TimeSpan JitteredExponentialDelay(TimeSpan baseDelay, int attemptNumber)
    {
        if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;
        var exponentialTicks = checked(baseDelay.Ticks * (1L << attemptNumber));
        var jitterFactor = 0.5d + Random.Shared.NextDouble();
        return TimeSpan.FromTicks((long)(exponentialTicks * jitterFactor));
    }
}
