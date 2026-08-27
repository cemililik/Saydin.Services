using System.Text;
using System.Text.Json;

namespace Saydin.Api.Helpers;

/// <summary>
/// Computes a conservative upper bound for PostgreSQL's uncompressed JSONB
/// representation. JSON text length is not a safe proxy: JSONB containers add a
/// four-byte entry per array item and two entries per object property, and numeric
/// values use PostgreSQL's binary Numeric representation.
/// </summary>
internal static class JsonbStorageSize
{
    private const long VarLenaHeaderBytes = 4;
    private const long ContainerHeaderBytes = 4;
    private const long EntryBytes = 4;
    private const long AlignmentPaddingBytes = 3;

    // varlena + long Numeric header, with headroom for representation details.
    private const long NumericOverheadBytes = 16;
    private const long NumericDigitBytes = 2;
    private const long DecimalDigitsPerNumericDigit = 4;

    public static long UpperBound(JsonElement element)
    {
        // A scalar root is represented as a one-element raw-scalar array.
        var root = element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? ContainerUpperBound(element)
            : Add(ContainerHeaderBytes + EntryBytes, PayloadUpperBound(element));
        return Add(VarLenaHeaderBytes, root);
    }

    private static long ContainerUpperBound(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            long size = ContainerHeaderBytes;
            foreach (var item in element.EnumerateArray())
                size = Add(size, EntryBytes, AlignmentPaddingBytes, PayloadUpperBound(item));
            return size;
        }

        long objectSize = ContainerHeaderBytes;
        foreach (var property in element.EnumerateObject())
        {
            objectSize = Add(
                objectSize,
                EntryBytes * 2,
                AlignmentPaddingBytes,
                Encoding.UTF8.GetByteCount(property.Name),
                AlignmentPaddingBytes,
                PayloadUpperBound(property.Value));
        }
        return objectSize;
    }

    private static long PayloadUpperBound(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object or JsonValueKind.Array => ContainerUpperBound(element),
        JsonValueKind.String => Encoding.UTF8.GetByteCount(element.GetString() ?? string.Empty),
        JsonValueKind.Number => NumericUpperBound(element.GetRawText().AsSpan()),
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
            or JsonValueKind.Undefined => 0,
        _ => throw new InvalidOperationException($"Unsupported JSON value kind: {element.ValueKind}"),
    };

    /// <summary>
    /// Bounds a PostgreSQL Numeric without expanding scientific notation or
    /// allocating BigInteger/decimal buffers. PostgreSQL currently represents
    /// implied exponent zeroes through Numeric weight, but accounting for the
    /// expanded decimal span keeps this precheck conservative across valid input
    /// extremes and representation changes.
    /// </summary>
    private static long NumericUpperBound(ReadOnlySpan<char> raw)
    {
        var index = raw.Length > 0 && raw[0] == '-' ? 1 : 0;
        long integerDigits = 0;
        long fractionalDigits = 0;
        var inFraction = false;
        var hasNonZeroDigit = false;

        while (index < raw.Length && raw[index] is not 'e' and not 'E')
        {
            var current = raw[index++];
            if (current == '.')
            {
                inFraction = true;
                continue;
            }

            hasNonZeroDigit |= current != '0';
            if (inFraction) fractionalDigits++;
            else integerDigits++;
        }

        if (!hasNonZeroDigit)
            return Add(NumericOverheadBytes, NumericDigitBytes);

        var exponentNegative = false;
        long exponentMagnitude = 0;
        if (index < raw.Length)
        {
            index++; // e/E
            if (index < raw.Length && raw[index] is '+' or '-')
            {
                exponentNegative = raw[index] == '-';
                index++;
            }

            while (index < raw.Length)
            {
                var digit = raw[index++] - '0';
                if (exponentMagnitude > (long.MaxValue - digit) / 10)
                {
                    exponentMagnitude = long.MaxValue;
                    break;
                }
                exponentMagnitude = exponentMagnitude * 10 + digit;
            }
        }

        long digitsBeforeDecimal;
        long digitsAfterDecimal;
        if (exponentNegative)
        {
            digitsBeforeDecimal = exponentMagnitude < integerDigits
                ? integerDigits - exponentMagnitude
                : 1;
            digitsAfterDecimal = Add(fractionalDigits, exponentMagnitude);
        }
        else
        {
            digitsBeforeDecimal = Add(integerDigits, exponentMagnitude);
            digitsAfterDecimal = exponentMagnitude < fractionalDigits
                ? fractionalDigits - exponentMagnitude
                : 0;
        }

        var expandedDecimalDigits = Add(digitsBeforeDecimal, digitsAfterDecimal);
        if (expandedDecimalDigits == long.MaxValue)
            return long.MaxValue;

        // Numeric groups are aligned independently on each side of the decimal
        // point; ceil(total/4) would undercount values such as 12345.6.
        var numericDigits = Add(
            CeilingNumericDigits(digitsBeforeDecimal),
            CeilingNumericDigits(digitsAfterDecimal));
        var digitBytes = MultiplySaturating(numericDigits, NumericDigitBytes);
        return Add(NumericOverheadBytes, digitBytes);
    }

    private static long CeilingNumericDigits(long decimalDigits) => decimalDigits == 0
        ? 0
        : 1 + ((decimalDigits - 1) / DecimalDigitsPerNumericDigit);

    private static long MultiplySaturating(long left, long right) =>
        left == 0 || right <= long.MaxValue / left ? left * right : long.MaxValue;

    private static long Add(params long[] values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value > long.MaxValue - total)
                return long.MaxValue;
            total += value;
        }
        return total;
    }
}
