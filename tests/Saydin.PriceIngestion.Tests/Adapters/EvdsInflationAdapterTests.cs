using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;

namespace Saydin.PriceIngestion.Tests.Adapters;

public class EvdsInflationAdapterTests
{
    private static readonly TimeProvider Clock =
        new FixedTimeProvider(new DateTimeOffset(2024, 4, 10, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Key_IsHeaderOnly_AndExactMonthsAreData()
    {
        var (adapter, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"items":[
                  {"Tarih":"2024-1","TP_FG_J0":"100.1"},
                  {"Tarih":"2024-2","TP_FG_J0":"101.2"},
                  {"Tarih":"2024-3","TP_FG_J0":"102.3"}]}
                """),
        });

        var result = await adapter.FetchRangeAsync(new(2024, 1, 1), new(2024, 3, 1), default);

        result.Kind.Should().Be(AdapterOutcomeKind.Data);
        result.Records.Should().HaveCount(3);
        var request = handler.Requests.Single();
        request.Headers.GetValues("key").Should().Equal("test-key");
        request.RequestUri!.Query.Should().NotContain("test-key");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, AdapterOutcomeKind.RetryableFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, AdapterOutcomeKind.RetryableFailure)]
    [InlineData(HttpStatusCode.Unauthorized, AdapterOutcomeKind.PermanentFailure)]
    public async Task HttpFailure_IsTyped(HttpStatusCode status, AdapterOutcomeKind expected)
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(status));
        (await adapter.FetchRangeAsync(new(2024, 1, 1), new(2024, 1, 1), default)).Kind
            .Should().Be(expected);
    }

    [Fact]
    public async Task LatestUnpublishedMonth_IsRetryable_NotExpectedNoData()
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"items":[]}"""),
        });

        var result = await adapter.FetchRangeAsync(new(2024, 3, 1), new(2024, 3, 1), default);

        result.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        result.Code.Should().Be("not_published_yet");
    }

    [Fact]
    public async Task HistoricalMissingMonth_IsPartialRejected()
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"items":[]}"""),
        });
        (await adapter.FetchRangeAsync(new(2020, 1, 1), new(2020, 1, 1), default)).Kind
            .Should().Be(AdapterOutcomeKind.PartialRejected);
    }

    [Fact]
    public async Task MissingKeyAndMalformedJson_ArePermanent()
    {
        var (missingKey, handler) = Build(_ => throw new InvalidOperationException(), null);
        (await missingKey.FetchRangeAsync(new(2020, 1, 1), new(2020, 1, 1), default)).Kind
            .Should().Be(AdapterOutcomeKind.PermanentFailure);
        handler.CallCount.Should().Be(0);

        var (malformed, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ broken"),
        });
        (await malformed.FetchRangeAsync(new(2020, 1, 1), new(2020, 1, 1), default)).Kind
            .Should().Be(AdapterOutcomeKind.PermanentFailure);
    }

    [Fact]
    public async Task OversizedTransportPayload_IsTypedRetryable()
    {
        var (adapter, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', ProviderTransportLimits.MaxResponseBytes + 1)),
        });

        var result = await adapter.FetchRangeAsync(new(2020, 1, 1), new(2020, 1, 1), default);

        result.Kind.Should().Be(AdapterOutcomeKind.RetryableFailure);
        result.Code.Should().Be("transport_payload_too_large");
    }

    [Fact]
    public async Task MapperContractAndWrongValueKind_AreTypedPermanent()
    {
        var (invalidValue, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"items":[{"Tarih":"2020-1","TP_FG_J0":"0"}]}"""),
        });
        var contract = await invalidValue.FetchRangeAsync(
            new(2020, 1, 1), new(2020, 1, 1), default);
        contract.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        contract.Code.Should().Be("contract_index_value_invalid");

        var (wrongKind, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"items":[{"Tarih":"2020-1","TP_FG_J0":{"bad":true}}]}"""),
        });
        var kind = await wrongKind.FetchRangeAsync(
            new(2020, 1, 1), new(2020, 1, 1), default);
        kind.Kind.Should().Be(AdapterOutcomeKind.PermanentFailure);
        kind.Code.Should().Be("contract_value_kind_invalid");
    }

    private static (EvdsInflationAdapter, StubHttpMessageHandler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string? key = "test-key")
    {
        var handler = new StubHttpMessageHandler(responder);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://evds3.tcmb.gov.tr/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("evds").Returns(client);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ExternalApis:Evds:ApiKey"] = key }).Build();
        return (new EvdsInflationAdapter(factory, config, Clock,
            NullLogger<EvdsInflationAdapter>.Instance), handler);
    }
}
