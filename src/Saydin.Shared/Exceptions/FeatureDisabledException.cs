namespace Saydin.Shared.Exceptions;

/// <summary>
/// Kullanıcının tier'ında devre dışı bırakılmış bir özellik talep edildiğinde fırlatılır
/// (HTTP 403 Forbidden üretir). `Message` teknik kullanım içindir.
/// </summary>
public sealed class FeatureDisabledException(string detail, string? featureKey = null)
    : Exception(featureKey is null ? detail : $"Feature '{featureKey}' disabled: {detail}")
{
    public string Detail { get; } = detail;
    public string? FeatureKey { get; } = featureKey;
}
