using System.Globalization;
using System.Text;
using System.Text.Json;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

internal sealed record ObservationEvidenceValue(string Json);

internal static class ObservationEvidence
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "as_of_at", "base_currency", "close", "currency", "date", "exchange",
        "exchange_timezone", "high", "index_value", "instrument_type", "interval", "low",
        "mic_code", "observation_id", "open", "provider_source", "quote_currency",
        "series", "source_timestamp_ms", "symbol", "unit", "volume",
    };

    public static ObservationEvidenceValue Create(params (string Key, object? Value)[] fields)
    {
        if (fields.Length == 0
            || fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(field => !AllowedKeys.Contains(field.Key)))
            throw new ProviderContractException("source_raw_key_rejected");

        // Stable transport/evidence text helps deterministic tests, but it is not the
        // persisted authority hash. PostgreSQL canonicalizes jsonb numeric scale and
        // computes observation_sha256 at write time.
        var json = new StringBuilder("{");
        var ordered = fields.OrderBy(field => field.Key, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (index > 0) json.Append(", ");
            json.Append(JsonSerializer.Serialize(ordered[index].Key));
            json.Append(": ");
            json.Append(ScalarJson(ordered[index].Value));
        }
        json.Append('}');
        var text = json.ToString();
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > ObservationAuthorityLimits.SourceRawBytes)
            throw new ProviderPayloadTooLargeException();
        return new ObservationEvidenceValue(text);
    }

    private static string ScalarJson(object? value) => value switch
    {
        null => "null",
        string text => JsonSerializer.Serialize(text),
        bool boolean => boolean ? "true" : "false",
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        DateOnly date => JsonSerializer.Serialize(
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTimeOffset instant => JsonSerializer.Serialize(
            instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        _ => throw new ProviderContractException("source_raw_value_rejected"),
    };
}
