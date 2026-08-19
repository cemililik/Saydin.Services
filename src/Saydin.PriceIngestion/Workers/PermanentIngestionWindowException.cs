namespace Saydin.PriceIngestion.Workers;

public sealed class PermanentIngestionWindowException(
    string source, Guid? assetId, DateOnly from, DateOnly to, string outcomeCode)
    : InvalidOperationException(
        $"Permanent ingestion window blocks the lane: {source}/{assetId} {from:yyyy-MM-dd}..{to:yyyy-MM-dd} ({outcomeCode})");
