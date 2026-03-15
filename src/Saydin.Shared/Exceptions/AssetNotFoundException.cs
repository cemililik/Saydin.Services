namespace Saydin.Shared.Exceptions;

public sealed class AssetNotFoundException(string symbol)
    : Exception($"'{symbol}' sembolüne sahip aktif bir varlık bulunamadı.")
{
    public string Symbol { get; } = symbol;
}
