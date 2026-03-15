namespace Saydin.Shared.Exceptions;

public sealed class ExternalApiException(string source, string message, Exception? innerException = null)
    : Exception($"[{source}] {message}", innerException)
{
    public string ApiSource { get; } = source;
}
