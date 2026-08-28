namespace Saydin.Api.Exceptions;

public sealed class RequestBodyTooLargeException(int maxBytes) : Exception
{
    public int MaxBytes { get; } = maxBytes;
}
