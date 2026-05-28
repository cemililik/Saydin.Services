using FluentAssertions;
using Saydin.PriceIngestion.Workers;

namespace Saydin.PriceIngestion.Tests.Workers;

/// <summary>
/// F2.4-9 ([G-D-04]): Gap-aware backfill yardımcısı `ComputeMissingRanges`
/// edge case'leri.
/// </summary>
public class BaseAssetWorkerTests
{
    [Fact]
    public void ComputeMissingRanges_NoExisting_ReturnsFullRange()
    {
        var ranges = BaseAssetWorker.ComputeMissingRanges(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5),
            new HashSet<DateOnly>());

        ranges.Should().ContainSingle();
        ranges[0].Should().Be((new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5)));
    }

    [Fact]
    public void ComputeMissingRanges_AllExisting_ReturnsEmpty()
    {
        var existing = new HashSet<DateOnly>
        {
            new(2024, 1, 1), new(2024, 1, 2), new(2024, 1, 3),
            new(2024, 1, 4), new(2024, 1, 5)
        };

        var ranges = BaseAssetWorker.ComputeMissingRanges(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5), existing);

        ranges.Should().BeEmpty();
    }

    [Fact]
    public void ComputeMissingRanges_HoleInMiddle_ReturnsSingleRange()
    {
        var existing = new HashSet<DateOnly>
        {
            new(2024, 1, 1),
            new(2024, 1, 5)
        };

        var ranges = BaseAssetWorker.ComputeMissingRanges(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5), existing);

        ranges.Should().ContainSingle();
        ranges[0].Should().Be((new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));
    }

    [Fact]
    public void ComputeMissingRanges_MultipleHoles_ReturnsMultipleRanges()
    {
        var existing = new HashSet<DateOnly>
        {
            new(2024, 1, 2),
            new(2024, 1, 4),
            new(2024, 1, 6)
        };

        var ranges = BaseAssetWorker.ComputeMissingRanges(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 7), existing);

        ranges.Should().HaveCount(4);
        ranges[0].Should().Be((new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 1)));
        ranges[1].Should().Be((new DateOnly(2024, 1, 3), new DateOnly(2024, 1, 3)));
        ranges[2].Should().Be((new DateOnly(2024, 1, 5), new DateOnly(2024, 1, 5)));
        ranges[3].Should().Be((new DateOnly(2024, 1, 7), new DateOnly(2024, 1, 7)));
    }

    [Fact]
    public void ComputeMissingRanges_HoleAtStart_ReturnsCorrectRange()
    {
        var existing = new HashSet<DateOnly>
        {
            new(2024, 1, 4), new(2024, 1, 5)
        };

        var ranges = BaseAssetWorker.ComputeMissingRanges(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5), existing);

        ranges.Should().ContainSingle();
        ranges[0].Should().Be((new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 3)));
    }

    [Fact]
    public void ComputeMissingRanges_HoleAtEnd_ReturnsCorrectRange()
    {
        var existing = new HashSet<DateOnly>
        {
            new(2024, 1, 1), new(2024, 1, 2)
        };

        var ranges = BaseAssetWorker.ComputeMissingRanges(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5), existing);

        ranges.Should().ContainSingle();
        ranges[0].Should().Be((new DateOnly(2024, 1, 3), new DateOnly(2024, 1, 5)));
    }

    [Fact]
    public void ComputeMissingRanges_SingleDayRange_AllExisting_ReturnsEmpty()
    {
        var ranges = BaseAssetWorker.ComputeMissingRanges(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 1),
            new HashSet<DateOnly> { new(2024, 1, 1) });

        ranges.Should().BeEmpty();
    }
}
