namespace Saydin.Shared.Entities;

public sealed class PricePoint
{
    public Guid AssetId { get; init; }
    public DateOnly PriceDate { get; init; }
    public decimal Close { get; init; }   // Tüm hesaplamalar için kanonik fiyat
    public decimal? Open { get; init; }
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public decimal? Volume { get; init; }
}
