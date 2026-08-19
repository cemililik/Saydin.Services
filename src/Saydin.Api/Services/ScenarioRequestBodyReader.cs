using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using Saydin.Api.Exceptions;
using Saydin.Api.Models.Requests;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

internal static class ScenarioRequestBodyReader
{
    internal const int MaxBodyBytes = 32 * 1024;
    // Request root + ExtraData'nın sekiz seviyesini ve bir güvenlik payını kapsar;
    // default serializer MaxDepth=64 bu endpoint için gereksiz geniştir.
    internal const int MaxRequestJsonDepth = ScenarioExtraDataValidator.MaxDepth + 2;

    public static async ValueTask<SaveScenarioRequest> ReadAsync(
        HttpRequest request,
        JsonSerializerOptions serializerOptions,
        IStringLocalizer<ErrorMessages> localizer,
        CancellationToken ct)
    {
        if (request.ContentLength is > MaxBodyBytes)
            throw new RequestBodyTooLargeException(MaxBodyBytes);

        var buffer = ArrayPool<byte>.Shared.Rent(MaxBodyBytes + 1);
        try
        {
            var length = 0;
            while (length <= MaxBodyBytes)
            {
                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(length, MaxBodyBytes + 1 - length), ct);
                if (read == 0)
                    break;

                length += read;
                if (length > MaxBodyBytes)
                    throw new RequestBodyTooLargeException(MaxBodyBytes);
            }

            if (length == 0)
                throw new ValidationException(
                    string.Format(localizer["RequestPayloadMissing"], "request"), field: "request");

            try
            {
                using var document = JsonDocument.Parse(
                    buffer.AsMemory(0, length),
                    new JsonDocumentOptions { MaxDepth = MaxRequestJsonDepth });
                EnsureNoDuplicateProperties(document.RootElement, localizer);

                var endpointOptions = new JsonSerializerOptions(serializerOptions)
                {
                    MaxDepth = MaxRequestJsonDepth,
                };
                return JsonSerializer.Deserialize<SaveScenarioRequest>(
                           buffer.AsSpan(0, length), endpointOptions)
                       ?? throw new ValidationException(
                           string.Format(localizer["RequestPayloadMissing"], "request"), field: "request");
            }
            catch (JsonException)
            {
                throw new ValidationException(localizer["MalformedJsonBody"], field: "request");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void EnsureNoDuplicateProperties(
        JsonElement element,
        IStringLocalizer<ErrorMessages> localizer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Web defaults bind CLR request properties case-insensitively. The duplicate
            // detector must use the same equivalence relation or `extraData` + `ExtraData`
            // would silently become last-wins before domain validation.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ValidationException(localizer["DuplicateJsonProperty"], field: "request");
                EnsureNoDuplicateProperties(property.Value, localizer);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                EnsureNoDuplicateProperties(item, localizer);
        }
    }
}
