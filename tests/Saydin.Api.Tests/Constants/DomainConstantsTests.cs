using FluentAssertions;
using Saydin.Shared.Constants;

namespace Saydin.Api.Tests.Constants;

/// <summary>
/// F3.1-2: domain sabit kümeleri (PriceIntervals, QuantityUnits.DcaAccepted, InflationSources)
/// için kontrat testleri. Değerlerin lowercase kaldığını (cache key / DB CHECK / case-insensitive
/// karşılaştırma varsayımı) ve lookup tutarlılığını doğrular.
/// </summary>
public class DomainConstantsTests
{
    [Theory]
    [InlineData(PriceIntervals.Daily)]
    [InlineData(PriceIntervals.Weekly)]
    [InlineData(PriceIntervals.Monthly)]
    public void PriceIntervals_Values_AreLowercase(string value)
    {
        value.Should().Be(value.ToLowerInvariant());
    }

    [Fact]
    public void PriceIntervals_OnlyDailyIsSupported()
    {
        PriceIntervals.SupportedLookup.Should().ContainSingle().Which.Should().Be(PriceIntervals.Daily);
        PriceIntervals.SupportedLookup.Contains(PriceIntervals.Weekly).Should().BeFalse();
        PriceIntervals.SupportedLookup.Contains(PriceIntervals.Monthly).Should().BeFalse();
    }

    [Fact]
    public void PriceIntervals_All_ContainsEveryDefinedValue()
    {
        PriceIntervals.All.Should().BeEquivalentTo(
            new[] { PriceIntervals.Daily, PriceIntervals.Weekly, PriceIntervals.Monthly });
    }

    [Fact]
    public void QuantityUnits_DcaAccepted_IsTryOnly()
    {
        QuantityUnits.DcaAccepted.Should().ContainSingle().Which.Should().Be(QuantityUnits.Try);
    }

    [Fact]
    public void QuantityUnits_WhatIfAccepted_ContainsTryUnitsGrams()
    {
        QuantityUnits.WhatIfAccepted.Should().BeEquivalentTo(
            new[] { QuantityUnits.Try, QuantityUnits.Units, QuantityUnits.Grams });
    }

    [Theory]
    [InlineData(QuantityUnits.Try)]
    [InlineData(QuantityUnits.Units)]
    [InlineData(QuantityUnits.Grams)]
    public void QuantityUnits_Values_AreLowercase(string value)
    {
        value.Should().Be(value.ToLowerInvariant());
    }

    [Fact]
    public void InflationSources_LookupContainsBothSources()
    {
        InflationSources.Lookup.Should().Contain(InflationSources.Tuik);
        InflationSources.Lookup.Should().Contain(InflationSources.SeedApproximation);
        InflationSources.Lookup.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(InflationSources.Tuik)]
    [InlineData(InflationSources.SeedApproximation)]
    public void InflationSources_Values_AreLowercase(string value)
    {
        // DB CHECK + composite PK literal'leriyle byte-for-byte uyum için lowercase olmalı.
        value.Should().Be(value.ToLowerInvariant());
    }
}
