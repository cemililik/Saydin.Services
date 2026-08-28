using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Saydin.Shared.Entities;

public sealed class PricePoint
{
    private string? _sourceRaw;

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
    // Ingestion writes the authoritative provider evidence to PostgreSQL, but API
    // cache/JSON material must never copy that potentially large raw payload.
    [JsonIgnore]
    public string? SourceRaw
    {
        get => _sourceRaw;
        set
        {
            _sourceRaw = value;
            if (value is not null)
                HasSourceRaw = true;
        }
    }

    // API read projections carry only the authority-presence bit required by the
    // final-observation boundary. This property is deliberately not persisted.
    [NotMapped]
    public bool HasSourceRaw { get; set; }
    public DateTimeOffset IngestedAt { get; private set; }

    // Navigation
    public Asset Asset { get; init; } = null!;
}
