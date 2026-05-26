namespace Saydin.Shared.Exceptions;

/// <summary>
/// Belirli bir tarih + asset için fiyat bulunamadığında fırlatılır.
/// `Message` teknik kullanım içindir (log/stack trace); kullanıcıya dönecek
/// metin <see cref="Saydin.Api.Exceptions.PriceNotFoundExceptionHandler"/>
/// tarafından <c>IStringLocalizer</c> ile formatlanır.
/// </summary>
public sealed class PriceNotFoundException(
    string assetSymbol,
    DateOnly date,
    IReadOnlyList<DateOnly>? nearestAvailableDates = null)
    : Exception($"Price not found for asset '{assetSymbol}' on {date:yyyy-MM-dd}.")
{
    public string AssetSymbol { get; } = assetSymbol;
    public DateOnly Date { get; } = date;
    public IReadOnlyList<DateOnly> NearestAvailableDates { get; } =
        nearestAvailableDates ?? [];
}
