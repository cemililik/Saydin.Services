using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Saydin.DataQualityAudit;

internal static class CanonicalJson
{
    public static byte[] Canonicalize(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            WriteElement(writer, document.RootElement);
        }

        return stream.ToArray();
    }

    public static byte[] SerializeCanonical<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        return Canonicalize(json);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                    != properties.Length)
                    throw new AuditRejectedException(
                        "manifest_duplicate_property", AuditExitCodes.InvalidArguments);
                foreach (var property in properties.OrderBy(
                             property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var integer))
                    throw new AuditRejectedException("manifest_number_not_integer", AuditExitCodes.InvalidArguments);
                writer.WriteNumberValue(integer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new AuditRejectedException("manifest_json_kind_invalid", AuditExitCodes.InvalidArguments);
        }
    }
}
