namespace Saydin.Shared.Exceptions;

/// <summary>
/// Belirli sembole sahip aktif asset bulunamadığında fırlatılır.
/// `Message` teknik kullanım içindir (log/stack trace).
/// </summary>
public sealed class AssetNotFoundException(string symbol)
    : Exception($"No active asset found with symbol '{symbol}'.")
{
    public string Symbol { get; } = symbol;
}
