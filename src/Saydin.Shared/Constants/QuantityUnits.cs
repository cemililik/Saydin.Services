namespace Saydin.Shared.Constants;

/// <summary>
/// `saved_scenarios.quantity_unit` (ve WhatIf/DCA `AmountType`) için kanonik değerler.
/// what_if için `try`, `units`, `grams` geçerli; DCA için yalnızca `try` desteklenir.
/// comparison/portfolio senaryoları toplu portföy verisi tuttukları için `try`
/// kullanılır.
/// </summary>
public static class QuantityUnits
{
    public const string Try    = "try";
    public const string Units  = "units";
    public const string Grams  = "grams";

    /// <summary>WhatIf single-asset hesaplamaları için kabul edilen tüm tipler.</summary>
    public static readonly IReadOnlySet<string> WhatIfAccepted =
        new HashSet<string>(StringComparer.Ordinal) { Try, Units, Grams };

    /// <summary>
    /// DB CHECK constraint için (saved_scenarios.quantity_unit) izin verilen tüm değerler.
    /// comparison/portfolio senaryoları da `try` ile saklanır.
    /// </summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Try, Units, Grams };
}
