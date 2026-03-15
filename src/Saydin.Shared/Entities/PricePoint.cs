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

    // Navigation
    public Asset Asset { get; init; } = null!;
}
