using System.Net;
using System.Text.Json;
using FluentAssertions;
using Saydin.Api.IntegrationTests.Fixtures;

namespace Saydin.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class AuthorityHttpIntegrationTests(
    DatabaseFixture database,
    ErrorContractWebAppFactory factory) : IClassFixture<ErrorContractWebAppFactory>
{
    [SkippableFact]
    public async Task ExactPrice_AddsBoundedFinalBasisWithoutInternalEvidence()
    {
        Skip.IfNot(database.Available, database.SkipReason);
        Skip.IfNot(database.PriceAuthority, "Frozen migration 020 authority fingerprint is required.");
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        Skip.IfNot(await ErrorContractHttpTests.ApiTrustReadyAsync(database),
            "Migration 021 API trust contract is required.");
        await using var scenario = await AuthorityObservationScenario.CreateAsync(database);
        var client = factory.CreateClient();
        var installation = await ErrorContractHttpTests.RegisterAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/assets/{scenario.Symbol}/price/{AuthorityObservationScenario.FirstFinalPriceDate:yyyy-MM-dd}");
        ErrorContractHttpTests.Authorize(request, installation.Credential);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var basis = document.RootElement.GetProperty("basis");
        basis.GetProperty("dataStatus").GetString().Should().Be("final");
        basis.GetProperty("providerSource").GetString().Should().Be("coingecko");
        basis.GetProperty("priceKind").GetString().Should().Be("daily_utc_reference");
        basis.GetProperty("authorityContractVersion").GetInt32().Should().Be(1);
        var lowerJson = json.ToLowerInvariant();
        lowerJson.Should().NotContain("sourceraw");
        lowerJson.Should().NotContain("sourceobservationid");
        lowerJson.Should().NotContain("sha256");
        lowerJson.Should().NotContain("payload");
    }
}
