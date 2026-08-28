using System.Globalization;
using System.Text.Json;
using Saydin.PriceIngestion.Adapters;

namespace Saydin.PriceIngestion.Mappers;

internal static class ProviderValueParser
{
    // Provider financial values use invariant decimal notation. Thousands
    // separators and parentheses are deliberately excluded: accepting either
    // can silently turn a locale-formatted value into a different amount.
    internal const NumberStyles FinancialNumberStyles =
        NumberStyles.AllowLeadingWhite
        | NumberStyles.AllowTrailingWhite
        | NumberStyles.AllowLeadingSign
        | NumberStyles.AllowDecimalPoint;

    internal static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                value = default;
                return false;
            case JsonValueKind.Number:
                return element.TryGetDecimal(out value);
            case JsonValueKind.String:
                return decimal.TryParse(element.GetString(), FinancialNumberStyles,
                    CultureInfo.InvariantCulture, out value);
            default:
                throw new ProviderContractException("contract_value_kind_invalid");
        }
    }

    internal static string? ReadString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String)
            throw new ProviderContractException("contract_value_kind_invalid");
        return element.GetString();
    }
}
