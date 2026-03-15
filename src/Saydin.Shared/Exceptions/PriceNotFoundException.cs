namespace Saydin.Shared.Exceptions;

public sealed class PriceNotFoundException(
    string assetSymbol,
    DateOnly date,
    IReadOnlyList<DateOnly>? nearestAvailableDates = null)
    : Exception($"{date:yyyy-MM-dd} tarihinde {assetSymbol} fiyatı bulunamadı.")
{
    public string AssetSymbol { get; } = assetSymbol;
    public DateOnly Date { get; } = date;
    public IReadOnlyList<DateOnly> NearestAvailableDates { get; } =
        nearestAvailableDates ?? [];
}
