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
    public async Task FifthLogicalFailureOpens_SixthHasZeroWire_ThenHalfOpenCloses()
    {
        var status = HttpStatusCode.ServiceUnavailable;
        var (client, handler, time) = Build(() => status);
        for (var logical = 0; logical < 5; logical++)
        {
            using var response = await client.GetAsync("https://provider.test/data");
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        handler.CallCount.Should().Be(20);

        var openCall = () => client.GetAsync("https://provider.test/data");
        await openCall.Should().ThrowAsync<BrokenCircuitException>();
        handler.CallCount.Should().Be(20);

        status = HttpStatusCode.OK;
        time.Advance(HttpResilienceExtensions.BreakDuration);
        using var probe = await client.GetAsync("https://provider.test/data");
        probe.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.CallCount.Should().Be(21);
        using var closed = await client.GetAsync("https://provider.test/data");
        closed.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.CallCount.Should().Be(22);
    }

    [Fact]
    public void RetryAfter_IsBoundedToThirtySeconds()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromMinutes(10));

        HttpResilienceExtensions.ResolveRetryDelay(response)
            .Should().Be(HttpResilienceExtensions.MaxRetryAfter);
    }

    private static (HttpClient Client, StubHttpMessageHandler Handler, FakeTimeProvider Time) Build(
        Func<HttpStatusCode> status)
    {
        var time = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status()));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddSaydinResilience(time);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("test"), handler, time);
    }
}
