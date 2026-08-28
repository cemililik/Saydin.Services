using System.Text;
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
    internal static int GetStorageUtf8Size(JsonElement element)
    {
        var counter = new PostgresJsonbTextSizeCounter();
        counter.Write(element);
        return counter.ByteCount;
    }

    /// <summary>
    /// Counts PostgreSQL's compact <c>jsonb::text</c> representation without
    /// materializing a second serialized payload. The counter saturates one byte
    /// above the application limit, so oversized or unrepresentable values remain
    /// fail-closed without attacker-controlled output allocations.
    /// </summary>
    private ref struct PostgresJsonbTextSizeCounter
    {
        private const int ExceededLimit = MaxUtf8Bytes + 1;
        private int _byteCount;

        public readonly int ByteCount => _byteCount;

        public void Write(JsonElement element)
        {
            if (_byteCount == ExceededLimit)
                return;

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    WriteObject(element);
                    break;
                case JsonValueKind.Array:
                    WriteArray(element);
                    break;
                case JsonValueKind.String:
                    WriteString(element.GetString()!);
                    break;
                case JsonValueKind.Number:
                    WriteNumber(element);
                    break;
                case JsonValueKind.True:
                    Add(4);
                    break;
                case JsonValueKind.False:
                    Add(5);
                    break;
                case JsonValueKind.Null:
                    Add(4);
                    break;
                default:
                    ExceedLimit();
                    break;
            }
        }

        private void WriteObject(JsonElement element)
        {
            Add(1); // {
            var first = true;
            foreach (var property in element.EnumerateObject())
            {
                if (_byteCount == ExceededLimit)
                    return;
                if (!first)
                    Add(2); // , + space
                WriteString(property.Name);
                Add(2); // : + space
                Write(property.Value);
                first = false;
            }
            Add(1); // }
        }

        private void WriteArray(JsonElement element)
        {
            Add(1); // [
            var first = true;
            foreach (var item in element.EnumerateArray())
            {
                if (_byteCount == ExceededLimit)
                    return;
                if (!first)
                    Add(2); // , + space
                Write(item);
                first = false;
            }
            Add(1); // ]
        }

        private void WriteString(string value)
        {
            Add(1); // opening quote
            for (var i = 0; i < value.Length && _byteCount != ExceededLimit; i++)
            {
                var current = value[i];
                switch (current)
                {
                    case '\"':
                    case '\\':
                    case '\b':
                    case '\f':
                    case '\n':
                    case '\r':
                    case '\t':
                        Add(2);
                        break;
                    case '\0':
                        // PostgreSQL jsonb rejects U+0000 even when it is escaped.
                        ExceedLimit();
                        break;
                    default:
                        if (current < 0x20)
                        {
                            Add(6); // \u00xx
                        }
                        else if (current < 0x80)
                        {
                            Add(1);
                        }
                        else if (current < 0x800)
                        {
                            Add(2);
                        }
                        else if (char.IsHighSurrogate(current))
                        {
                            if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                            {
                                ExceedLimit();
                                break;
                            }
                            Add(4);
                            i++;
                        }
                        else if (char.IsLowSurrogate(current))
                        {
                            ExceedLimit();
                        }
                        else
                        {
                            Add(3);
                        }
                        break;
                }
            }
            Add(1); // closing quote
        }

        private void WriteNumber(JsonElement element)
        {
            // Parsing through System.Decimal is not exact: for example TryGetDecimal
            // silently underflows 1e-100 to zero. Count the original JSON numeric
            // lexeme instead so PostgreSQL's scale/exponent expansion is preserved.
            // ScenarioRequestBodyReader caps the source at 32 KiB; the counter also
            // refuses a single numeric token larger than the storage budget.
            var rawText = element.GetRawText();
            if (rawText.Length > MaxUtf8Bytes)
            {
                ExceedLimit();
                return;
            }

            var raw = rawText.AsSpan();
            var coefficientStart = raw[0] == '-' ? 1 : 0;
            var exponentStart = raw.Length;
            var decimalPoint = -1;
            for (var i = coefficientStart; i < raw.Length; i++)
            {
                if (raw[i] == '.')
                    decimalPoint = i;
                else if (raw[i] is 'e' or 'E')
                {
                    exponentStart = i;
                    break;
                }
            }

            var integerDigits = (decimalPoint >= 0 ? decimalPoint : exponentStart)
                - coefficientStart;
            var fractionDigits = decimalPoint >= 0 ? exponentStart - decimalPoint - 1 : 0;
            var totalDigits = integerDigits + fractionDigits;
            var firstNonZero = totalDigits;
            var digitPosition = 0;
            for (var i = coefficientStart; i < exponentStart; i++)
            {
                if (raw[i] == '.')
                    continue;
                if (firstNonZero == totalDigits && raw[i] != '0')
                    firstNonZero = digitPosition;
                digitPosition++;
            }

            if (!TryReadExponent(raw, exponentStart, out var exponent))
            {
                ExceedLimit();
                return;
            }

            long scale;
            try
            {
                scale = checked((long)fractionDigits - exponent);
            }
            catch (OverflowException)
            {
                ExceedLimit();
                return;
            }
            var isZero = firstNonZero == totalDigits;
            if (isZero)
            {
                if (scale <= 0)
                    Add(1);
                else if (scale > MaxUtf8Bytes)
                    ExceedLimit();
                else
                    Add((int)scale + 2); // 0. + scale digits
                return;
            }

            if (scale is > MaxUtf8Bytes or < -MaxUtf8Bytes)
            {
                ExceedLimit();
                return;
            }

            var decimalPosition = totalDigits - scale;
            var renderedIntegerDigits = decimalPosition > firstNonZero
                ? decimalPosition - firstNonZero
                : 1;
            if (renderedIntegerDigits > MaxUtf8Bytes)
            {
                ExceedLimit();
                return;
            }

            var renderedFractionBytes = scale > 0 ? scale + 1 : 0; // dot + scale digits
            var renderedBytes = (raw[0] == '-' ? 1L : 0)
                + renderedIntegerDigits
                + renderedFractionBytes;
            if (renderedBytes > MaxUtf8Bytes)
                ExceedLimit();
            else
                Add((int)renderedBytes);
        }

        private static bool TryReadExponent(
            ReadOnlySpan<char> raw,
            int exponentStart,
            out long exponent)
        {
            exponent = 0;
            if (exponentStart == raw.Length)
                return true;

            var index = exponentStart + 1;
            var isNegative = false;
            if (raw[index] is '+' or '-')
            {
                isNegative = raw[index] == '-';
                index++;
            }

            for (; index < raw.Length; index++)
            {
                var digit = raw[index] - '0';
                if (exponent > (long.MaxValue - digit) / 10)
                    return false;
                exponent = (exponent * 10) + digit;
            }
            if (isNegative)
                exponent = -exponent;
            return true;
        }

        private void Add(int bytes)
        {
            if (_byteCount == ExceededLimit)
                return;
            if (bytes > MaxUtf8Bytes - _byteCount)
            {
                ExceedLimit();
                return;
            }
            _byteCount += bytes;
        }

        private void ExceedLimit() => _byteCount = ExceededLimit;
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
