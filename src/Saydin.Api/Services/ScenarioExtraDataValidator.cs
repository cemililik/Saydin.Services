using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using Saydin.Shared.Constants;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

/// <summary>
/// Validates the repository-proven mobile contract (legacy-v1) and enforces
/// resource budgets before any user/last_seen/database write occurs.
/// </summary>
internal static class ScenarioExtraDataValidator
{
    internal const int MaxUtf8Bytes = 8 * 1024;
    internal const int MaxDepth = 8;
    internal const int MaxProperties = 64;
    internal const int MaxNodes = 256;
    internal const int MaxArrayItems = 128;
    internal const int MaxStringUtf8Bytes = 2048;

    private static readonly JsonSerializerOptions StorageJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RootFields =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ScenarioTypes.WhatIf] = new HashSet<string>(
                ["includeInflation", "mode"], StringComparer.Ordinal),
            [ScenarioTypes.Comparison] = new HashSet<string>(
                ["winnerSymbol", "winnerName", "winnerReturn", "includeInflation"], StringComparer.Ordinal),
            [ScenarioTypes.Dca] = new HashSet<string>(
                ["includeInflation", "period", "periodicAmount"], StringComparer.Ordinal),
            [ScenarioTypes.Portfolio] = new HashSet<string>(
                ["totalReturn", "includeInflation", "items"], StringComparer.Ordinal),
        };

    private static readonly IReadOnlySet<string> PortfolioItemFields = new HashSet<string>(
        ["assetSymbol", "assetDisplayName", "amount", "amountType"], StringComparer.Ordinal);

    public static void Validate(
        JsonElement? extraData,
        string scenarioType,
        IStringLocalizer<ErrorMessages> localizer)
    {
        if (!extraData.HasValue || extraData.Value.ValueKind == JsonValueKind.Null)
            return;

        var root = extraData.Value;
        if (root.ValueKind != JsonValueKind.Object)
            Fail(localizer, "ExtraDataMustBeObject");

        if (GetStorageUtf8Size(root) > MaxUtf8Bytes)
            Fail(localizer, "ExtraDataTooLarge", MaxUtf8Bytes);

        var budget = new Budget();
        ValidateNode(root, depth: 1, budget, localizer);
        ValidateLegacyV1Schema(root, scenarioType, localizer);
    }

    private static void ValidateNode(
        JsonElement element,
        int depth,
        Budget budget,
        IStringLocalizer<ErrorMessages> localizer)
    {
        if (depth > MaxDepth)
            Fail(localizer, "ExtraDataTooDeep", MaxDepth);

        if (++budget.Nodes > MaxNodes)
            Fail(localizer, "ExtraDataTooManyNodes", MaxNodes);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (++budget.Properties > MaxProperties)
                        Fail(localizer, "ExtraDataTooManyProperties", MaxProperties);
                    // Property adı da JSON ağacında saldırgan-kontrollü bir node'dur;
                    // böylece isim + değer toplamı node bütçesine dahil olur.
                    if (++budget.Nodes > MaxNodes)
                        Fail(localizer, "ExtraDataTooManyNodes", MaxNodes);
                    ValidateNode(property.Value, depth + 1, budget, localizer);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (++budget.ArrayItems > MaxArrayItems)
                        Fail(localizer, "ExtraDataTooManyArrayItems", MaxArrayItems);
                    ValidateNode(item, depth + 1, budget, localizer);
                }
                break;

            case JsonValueKind.String:
                if (Encoding.UTF8.GetByteCount(element.GetString()!) > MaxStringUtf8Bytes)
                    Fail(localizer, "ExtraDataStringTooLong", MaxStringUtf8Bytes);
                break;
        }
    }

    private static void ValidateLegacyV1Schema(
        JsonElement root,
        string scenarioType,
        IStringLocalizer<ErrorMessages> localizer)
    {
        var allowed = RootFields[scenarioType];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
                Fail(localizer, "DuplicateJsonProperty");
            if (!allowed.Contains(property.Name))
                Fail(localizer, "ExtraDataUnknownField");

            var valid = scenarioType switch
            {
                ScenarioTypes.WhatIf => ValidateWhatIfField(property),
                ScenarioTypes.Comparison => ValidateComparisonField(property),
                ScenarioTypes.Dca => ValidateDcaField(property),
                ScenarioTypes.Portfolio => ValidatePortfolioField(property, localizer),
                _ => false,
            };
            if (!valid)
                Fail(localizer, "ExtraDataInvalidFieldType");
        }
    }

    /// <summary>
    /// PostgreSQL <c>jsonb::text</c> writes decoded UTF-8 strings and one space after
    /// each colon/comma. Property ordering does not change byte count. Modeling that
    /// representation keeps the application limit compatible with migration 018's
    /// planned <c>octet_length(extra_data::text)</c> CHECK.
    /// </summary>
    internal static int GetStorageUtf8Size(JsonElement element) =>
        JsonSerializer.SerializeToUtf8Bytes(element, StorageJsonOptions).Length
        + CountPostgresJsonbFormattingSpaces(element);

    private static int CountPostgresJsonbFormattingSpaces(JsonElement element)
    {
        var spaces = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var count = 0;
            foreach (var property in element.EnumerateObject())
            {
                count++;
                spaces += CountPostgresJsonbFormattingSpaces(property.Value);
            }
            spaces += count; // `: ` after every property name.
            spaces += Math.Max(0, count - 1); // `, ` between properties.
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var item in element.EnumerateArray())
            {
                count++;
                spaces += CountPostgresJsonbFormattingSpaces(item);
            }
            spaces += Math.Max(0, count - 1); // `, ` between array items.
        }
        return spaces;
    }

    private static bool ValidateWhatIfField(JsonProperty property) => property.Name switch
    {
        "includeInflation" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "mode" => property.Value.ValueKind == JsonValueKind.String,
        _ => false,
    };

    private static bool ValidateComparisonField(JsonProperty property) => property.Name switch
    {
        "winnerSymbol" or "winnerName" => property.Value.ValueKind == JsonValueKind.String,
        "winnerReturn" => IsSupportedNumber(property.Value),
        "includeInflation" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };

    private static bool ValidateDcaField(JsonProperty property) => property.Name switch
    {
        "includeInflation" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "period" or "periodicAmount" => property.Value.ValueKind == JsonValueKind.String,
        _ => false,
    };

    private static bool ValidatePortfolioField(
        JsonProperty property,
        IStringLocalizer<ErrorMessages> localizer) => property.Name switch
    {
        "totalReturn" => IsSupportedNumber(property.Value),
        "includeInflation" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "items" => ValidatePortfolioItems(property.Value, localizer),
        _ => false,
    };

    private static bool ValidatePortfolioItems(
        JsonElement items,
        IStringLocalizer<ErrorMessages> localizer)
    {
        if (items.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    Fail(localizer, "DuplicateJsonProperty");
                if (!PortfolioItemFields.Contains(property.Name))
                    Fail(localizer, "ExtraDataUnknownField");
                if (property.Value.ValueKind != JsonValueKind.String)
                    return false;
            }
        }

        return true;
    }

    private static bool IsSupportedNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _);

    private static void Fail(
        IStringLocalizer<ErrorMessages> localizer,
        string key,
        params object[] args) => throw new ValidationException(
            args.Length == 0 ? localizer[key] : string.Format(localizer[key], args),
            field: "ExtraData");

    private sealed class Budget
    {
        public int Properties { get; set; }
        public int Nodes { get; set; }
        public int ArrayItems { get; set; }
    }
}
