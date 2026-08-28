using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Saydin.DataRepair;

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
            Write(writer, document.RootElement);
        return stream.ToArray();
    }

    public static byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo) =>
        Canonicalize(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count()
                    != properties.Length)
                    throw Rejected("json_duplicate_property");
                foreach (var property in properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var integer)) throw Rejected("json_number_not_integer");
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
                throw Rejected("json_kind_invalid");
        }
    }

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.InvalidArguments);
}
