namespace Saydin.Shared.Entities;

public sealed class PricePoint
{
    public Guid AssetId { get; init; }
    public DateOnly PriceDate { get; init; }
    public decimal Close { get; set; }   // set: UPSERT güncellemesi için
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal? Volume { get; set; }
    public string? ProviderSource { get; set; }
    public string? SourceObservationId { get; set; }
    public DateTimeOffset? AsOfAt { get; set; }
    public string? PriceKind { get; set; }
    public bool? IsFinal { get; set; }
    public byte[]? PayloadSha256 { get; set; }
    public int? PayloadByteLength { get; set; }
    public byte[]? ObservationSha256 { get; set; }
    public Guid? IngestionWindowId { get; set; }
    public int? AuthorityContractVersion { get; set; }
    public string? SourceRaw { get; set; }
    public DateTimeOffset IngestedAt { get; private set; }

    // Navigation
    public Asset Asset { get; init; } = null!;
}
