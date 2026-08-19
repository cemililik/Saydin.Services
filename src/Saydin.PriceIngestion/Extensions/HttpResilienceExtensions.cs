using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Saydin.PriceIngestion.Extensions;

internal static class HttpResilienceExtensions
{
    internal static readonly TimeSpan SamplingDuration = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    public static IHttpClientBuilder AddSaydinResilience(
        this IHttpClientBuilder builder,
        TimeProvider? timeProvider = null)
    {
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.AddResilienceHandler("provider-authority-v1", pipeline =>
        {
            if (timeProvider is not null) pipeline.TimeProvider = timeProvider;

            // Strategies are outer-to-inner: one exhausted retry chain is one breaker
            // sample. Five failed logical calls open the circuit; the sixth does no I/O.
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                ShouldHandle = TransientHttpPredicate(),
                FailureRatio = 1.0,
                MinimumThroughput = 5,
                SamplingDuration = SamplingDuration,
                BreakDuration = BreakDuration,
            });
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                ShouldHandle = TransientHttpPredicate(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false,
                ShouldRetryAfterHeader = false,
                DelayGenerator = arguments => new ValueTask<TimeSpan?>(
                    ResolveRetryDelay(arguments.Outcome.Result)),
            });
            pipeline.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
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

    internal static TimeSpan ResolveRetryDelay(HttpResponseMessage? response)
    {
        if (response?.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
            return TimeSpan.Zero;
        var requested = response.Headers.RetryAfter?.Delta;
        if (requested is null && response.Headers.RetryAfter?.Date is not null)
            return MaxRetryAfter;
        if (requested is null || requested <= TimeSpan.Zero) return TimeSpan.Zero;
        return requested > MaxRetryAfter ? MaxRetryAfter : requested.Value;
    }
}
