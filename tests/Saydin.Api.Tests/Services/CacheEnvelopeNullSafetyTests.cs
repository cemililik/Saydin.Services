using FluentAssertions;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Api.Tests.Helpers;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Services;

public sealed class CacheEnvelopeNullSafetyTests
{
    private static readonly AssetReadIdentity Identity = new(
        AuthorityTestData.DefaultAssetId,
        "USDTRY",
        ProviderSources.Tcmb);

    [Fact]
    public void PriceEnvelope_NullPoint_IsCacheMiss()
    {
        var entry = new PriceCacheEntry(
            Identity.Symbol, Identity.Source, Identity.Id,
            new DateOnly(2025, 1, 1), null, null);

        entry.IsValidExact(Identity, new DateOnly(2025, 1, 1)).Should().BeFalse();
    }

    [Fact]
    public void PriceRangeEnvelope_NullCollectionOrElement_IsCacheMiss()
    {
        var date = new DateOnly(2025, 1, 1);
        var nullCollection = new PriceRangeCacheEntry(
            Identity.Symbol, Identity.Source, Identity.Id, date, date, "daily", null);
        var nullElement = new PriceRangeCacheEntry(
            Identity.Symbol, Identity.Source, Identity.Id, date, date, "daily", [null]);

        nullCollection.IsValid(Identity, date, date, "daily").Should().BeFalse();
        nullElement.IsValid(Identity, date, date, "daily").Should().BeFalse();
    }

    [Fact]
    public void CalculationEnvelope_NullResponse_IsCacheMiss()
    {
        var date = new DateOnly(2025, 1, 1);
        var entry = new WhatIfCacheEntry(
            "USDTRY", date, date, 100m, "try", false, "tr", null);

        entry.IsValid("USDTRY", date, date, 100m, "try", false, "tr")
            .Should().BeFalse();
    }

    [Fact]
    public void CalculationContract_NullNestedMembers_AreCacheMisses()
    {
        var basis = new ObservationBasisSummaryResponse(
            AuthorityDataStatuses.Final,
            [ProviderSources.Tcmb],
            [ObservationPriceKinds.OfficialReference],
            1, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        CalculationCacheContract.IsComplete(
            new CalculationDataResponse(AuthorityDataStatuses.Complete, null!, basis, basis), false)
            .Should().BeFalse();
        CalculationCacheContract.IsComplete(
            new CalculationDataResponse(AuthorityDataStatuses.Complete, [], null!, basis), false)
            .Should().BeFalse();
        CalculationCacheContract.IsComplete(
            new CalculationDataResponse(AuthorityDataStatuses.Complete, [], basis, null!), false)
            .Should().BeFalse();
    }
}
