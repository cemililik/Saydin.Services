using FluentAssertions;
using Saydin.PriceIngestion.Workers;

namespace Saydin.PriceIngestion.Tests.Workers;

/// <summary>
/// INGR-012: EVDS backfill aralığının (potansiyel ~20 yıl) güvenli EVDS chunk'larına
/// bölünmesini doğrular (ComputeBackfillChunks saf fonksiyonu — ComputeMissingRanges pattern'i).
/// </summary>
public class EvdsInflationWorkerTests
{
    [Fact]
    public void ComputeBackfillChunks_FromAfterTo_ReturnsEmpty()
    {
        var chunks = EvdsInflationWorker.ComputeBackfillChunks(
            new DateOnly(2025, 6, 1), new DateOnly(2025, 1, 1), 60);

        chunks.Should().BeEmpty();
    }

    [Fact]
    public void ComputeBackfillChunks_RangeWithinOneChunk_ReturnsSingleChunkEndingAtTo()
    {
        var from = new DateOnly(2025, 1, 1);
        var to   = new DateOnly(2025, 6, 1);

        var chunks = EvdsInflationWorker.ComputeBackfillChunks(from, to, 60);

        chunks.Should().ContainSingle();
        chunks[0].Should().Be((from, to));
    }

    [Fact]
    public void ComputeBackfillChunks_ExactBoundaryPlusOne_SplitsIntoTwo()
    {
        var from = new DateOnly(2020, 1, 1);
        var to   = from.AddMonths(60); // 61 ay → 2 chunk (60 + 1)

        var chunks = EvdsInflationWorker.ComputeBackfillChunks(from, to, 60);

        chunks.Should().HaveCount(2);
        chunks[0].Should().Be((from, from.AddMonths(59)));
        chunks[1].Should().Be((from.AddMonths(60), to));
    }

    [Fact]
    public void ComputeBackfillChunks_MultiYear_ContiguousNonOverlappingEndingAtTo()
    {
        var from = new DateOnly(2006, 1, 1);
        var to   = new DateOnly(2026, 4, 1); // ~20 yıl

        var chunks = EvdsInflationWorker.ComputeBackfillChunks(from, to, 60);

        chunks.Should().HaveCountGreaterThan(1);
        chunks[0].From.Should().Be(from);
        chunks[^1].To.Should().Be(to);
        for (var i = 1; i < chunks.Count; i++)
            chunks[i].From.Should().Be(chunks[i - 1].To.AddMonths(1), "chunk'lar boşluksuz ve örtüşmesiz olmalı");
        foreach (var (cFrom, cTo) in chunks)
        {
            var months = (cTo.Year - cFrom.Year) * 12 + (cTo.Month - cFrom.Month) + 1;
            months.Should().BeLessThanOrEqualTo(60, "her chunk ≤ chunkMonths olmalı");
        }
    }

    [Fact]
    public void ComputeBackfillChunks_NonPositiveChunk_Throws()
    {
        var act = () => EvdsInflationWorker.ComputeBackfillChunks(
            new DateOnly(2025, 1, 1), new DateOnly(2025, 6, 1), 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
