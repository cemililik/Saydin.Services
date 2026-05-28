namespace Saydin.Shared.Exceptions;

/// <summary>
/// İstek alanları geçerlilik kontrolünden geçmediğinde fırlatılır (HTTP 400 üretir).
/// `Message` teknik kullanım içindir (log/stack trace); kullanıcıya dönecek metin
/// servis katmanında <c>IStringLocalizer</c> ile zaten formatlanmış olarak gelir
/// ve handler bunu <c>Detail</c> alanına yazar.
/// </summary>
public sealed class ValidationException(string detail, string? field = null)
    : Exception(field is null ? detail : $"{field}: {detail}")
{
    public string Detail { get; } = detail;
    public string? Field { get; } = field;
}
