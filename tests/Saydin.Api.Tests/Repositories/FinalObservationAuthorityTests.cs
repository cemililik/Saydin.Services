using FluentAssertions;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Repositories;

public sealed class FinalObservationAuthorityTests
{
    [Fact]
    public void PricePredicate_RejectsLegacyWrongSourceAndNonSha256Rows()
    {
        var assetId = Guid.NewGuid();
        var asset = new Asset
        {
            Id = assetId,
            Symbol = "USDTRY",
            DisplayName = "USD/TRY",
            Category = AssetCategory.Currency,
            Source = ProviderSources.Tcmb,
            SourceId = "USD",
            IsActive = true,
        };
        var valid = Price(asset, 32);
        var legacy = new PricePoint
        {
            AssetId = assetId,
            Asset = asset,
            PriceDate = new DateOnly(2026, 8, 18),
            Close = 41m,
        };
        var shortHash = Price(asset, 31);
        var wrongSourceAsset = new Asset
        {
            Id = Guid.NewGuid(),
            Symbol = "FORGED",
            DisplayName = "Forged",
            Category = AssetCategory.Currency,
            Source = ProviderSources.TwelveData,
            SourceId = "FORGED",
            IsActive = true,
        };
        var wrongSource = Price(wrongSourceAsset, 32);

        var visible = new[] { valid, legacy, shortHash, wrongSource }
            .AsQueryable()
            .WhereCompleteFinalAuthority()
            .ToArray();

        visible.Should().ContainSingle().Which.Should().BeSameAs(valid);
    }

    [Fact]
    public void InflationPredicate_RequiresExactEvdsTuikFinalCpiTupleAndSha256Length()
    {
        var valid = Inflation(32);
        var shortHash = Inflation(31);
        var wrongProvider = Inflation(32);
        wrongProvider.ProviderSource = ProviderSources.Tcmb;
        var legacy = new InflationRate
        {
            PeriodDate = new DateOnly(2026, 7, 1),
            IndexValue = 100m,
            Source = InflationSources.Tuik,
        };

        var visible = new[] { valid, shortHash, wrongProvider, legacy }
            .AsQueryable()
            .WhereCompleteFinalAuthority()
            .ToArray();

        visible.Should().ContainSingle().Which.Should().BeSameAs(valid);
    }

    [Fact]
    public void PublicBasisSerialization_IsAdditiveAndNeverContainsEvidenceOrHash()
    {
        var basis = AuthorityDataResponseFactory.Exact(new ObservationAuthorityValue(
            ProviderSources.Tcmb,
            ObservationPriceKinds.OfficialReference,
            new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero),
            2));

        var json = System.Text.Json.JsonSerializer.Serialize(basis,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        json.Should().Contain("\"dataStatus\":\"final\"");
        json.Should().Contain("\"providerSource\":\"tcmb\"");
        json.Should().Contain("\"authorityContractVersion\":2");
        json.Should().NotContain("sourceRaw");
        json.Should().NotContain("sourceObservationId");
        json.Should().NotContain("sha256");
    }

    [Fact]
    public void FinalSummary_TenThousandContractVersions_HasConstantSizeOutput()
    {
        var asOf = new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero);
        var values = Enumerable.Range(1, 10_000)
            .Select(version => new ObservationAuthorityValue(
                ProviderSources.Tcmb,
                ObservationPriceKinds.OfficialReference,
                asOf.AddMinutes(version),
                version));

        var summary = AuthorityDataResponseFactory.FinalSummary(values);
        var json = System.Text.Json.JsonSerializer.Serialize(summary,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        summary.ObservationCount.Should().Be(10_000);
        summary.MinAuthorityContractVersion.Should().Be(1);
        summary.MaxAuthorityContractVersion.Should().Be(10_000);
        summary.ProviderSources.Should().ContainSingle();
        summary.PriceKinds.Should().ContainSingle();
        json.Length.Should().BeLessThan(512, "summary output must remain O(1)");
        json.Should().NotContain("authorityContractVersions");
    }

    private static PricePoint Price(Asset asset, int hashLength) => new()
    {
        AssetId = asset.Id,
        Asset = asset,
        PriceDate = new DateOnly(2026, 8, 18),
        Close = 41m,
        ProviderSource = ProviderSources.Tcmb,
        SourceObservationId = "tcmb:USD:20260818",
        AsOfAt = new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero),
        PriceKind = ObservationPriceKinds.OfficialReference,
        IsFinal = true,
        ObservationSha256 = Enumerable.Repeat((byte)0x2a, hashLength).ToArray(),
        AuthorityContractVersion = 2,
        SourceRaw = "{}",
    };

    private static InflationRate Inflation(int hashLength) => new()
    {
        PeriodDate = new DateOnly(2026, 7, 1),
        IndexValue = 100m,
        Source = InflationSources.Tuik,
        ProviderSource = ProviderSources.Evds,
        SourceObservationId = "evds:TP.FG.J0:2026-07",
        AsOfAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        PriceKind = ObservationPriceKinds.CpiIndex,
        IsFinal = true,
        ObservationSha256 = Enumerable.Repeat((byte)0x2a, hashLength).ToArray(),
        AuthorityContractVersion = 2,
        SourceRaw = "{}",
    };
}
