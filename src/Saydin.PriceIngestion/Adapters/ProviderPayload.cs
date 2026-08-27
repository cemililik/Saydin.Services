using System.Buffers;
using System.Security.Cryptography;
using System.Text;
namespace Saydin.PriceIngestion.Adapters;

internal sealed record ProviderPayload(byte[] Bytes, byte[] Sha256)
{
    public string Utf8Text => Encoding.UTF8.GetString(Bytes);
}

internal static class BoundedHttpContent
{
    public static async Task<ProviderPayload> ReadAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > ProviderTransportLimits.MaxResponseBytes)
            throw new ProviderTransportPayloadTooLargeException();

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(8_192);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                if (output.Length + read > ProviderTransportLimits.MaxResponseBytes)
                    throw new ProviderTransportPayloadTooLargeException();
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return new ProviderPayload(output.ToArray(), hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

internal static class ProviderTransportLimits
{
    // This bounds one provider HTTP response, not one persisted observation's
    // evidence JSON. Keeping the authorities separate prevents schema-limit
    // changes from silently changing network behavior.
    internal const int MaxResponseBytes = 256 * 1024;
}

public sealed class ProviderTransportPayloadTooLargeException()
    : Exception("provider_transport_payload_too_large");

public sealed class ProviderPayloadTooLargeException()
    : Exception("provider_payload_too_large");

public sealed class ProviderContractException(string code)
    : Exception(code)
{
    public string Code { get; } = code;
}
