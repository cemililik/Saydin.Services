using FluentAssertions;
using Saydin.Api.Helpers;

namespace Saydin.Api.Tests.Helpers;

/// <summary>
/// F3.1-6: WhatIfCalculator ve DcaCalculator'dan çıkarılan ortak seyrekleştirme
/// (down-sampling) mantığının kontrat testleri. Önceden iki serviste birebir tekrar
/// eden indeks-matematiği tek noktada doğrulanır.
/// </summary>
public class ChartSamplerTests
{
    [Fact]
    public void Downsample_EmptySource_ReturnsEmpty()
    {
        var result = ChartSampler.Downsample(Array.Empty<int>(), x => x, maxPoints: 60);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Downsample_CountBelowMax_ReturnsAllProjectedInOrder()
    {
        var source = new[] { 1, 2, 3, 4, 5 };

        var result = ChartSampler.Downsample(source, x => x * 10, maxPoints: 60);

        result.Should().Equal(10, 20, 30, 40, 50);
    }

    [Fact]
    public void Downsample_CountEqualsMax_ReturnsAll()
    {
        var source = Enumerable.Range(1, 60).ToArray();

        var result = ChartSampler.Downsample(source, x => x, maxPoints: 60);

        result.Should().HaveCount(60);
        result.Should().Equal(source);
    }

    [Fact]
    public void Downsample_CountAboveMax_ReturnsExactlyMaxPoints()
    {
        var source = Enumerable.Range(0, 1000).ToArray();

        var result = ChartSampler.Downsample(source, x => x, maxPoints: 60);

        result.Should().HaveCount(60);
    }

    [Fact]
    public void Downsample_CountAboveMax_IncludesFirstAndLastPoint()
    {
        var source = Enumerable.Range(0, 1000).ToArray();

        var result = ChartSampler.Downsample(source, x => x, maxPoints: 60);

        result[0].Should().Be(0);            // ilk nokta
        result[^1].Should().Be(999);         // son nokta
    }

    [Fact]
    public void Downsample_CountAboveMax_IsMonotonicAndDeterministic()
    {
        var source = Enumerable.Range(0, 500).ToArray();

        var first  = ChartSampler.Downsample(source, x => x, maxPoints: 60);
        var second = ChartSampler.Downsample(source, x => x, maxPoints: 60);

        first.Should().Equal(second);                                  // deterministik
        first.Should().BeInAscendingOrder();                           // sıralı (kayma yok)
    }

    [Fact]
    public void Downsample_NullSelector_Throws()
    {
        var act = () => ChartSampler.Downsample<int, int>(new[] { 1 }, null!, maxPoints: 60);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Downsample_NonPositiveMaxPoints_Throws()
    {
        var act = () => ChartSampler.Downsample(new[] { 1, 2, 3 }, x => x, maxPoints: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Downsample_MaxPointsOne_WithLargerSource_ReturnsFirstElementOnly()
    {
        // Regresyon: maxPoints==1 + count>1 → (maxPoints-1)=0 → 0/0=NaN → (int)NaN=int.MinValue
        // → source[int.MinValue] çökerdi. Kısa devre ile tek nokta (ilk eleman) dönmeli.
        var source = Enumerable.Range(10, 50).ToArray(); // 10..59

        var result = ChartSampler.Downsample(source, x => x, maxPoints: 1);

        result.Should().ContainSingle().Which.Should().Be(10);
    }

    [Fact]
    public void Downsample_MaxPointsOne_WithSingleSource_ReturnsThatElement()
    {
        var result = ChartSampler.Downsample(new[] { 42 }, x => x, maxPoints: 1);

        result.Should().ContainSingle().Which.Should().Be(42);
    }
}
