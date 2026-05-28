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

    /// <summary>WhatIf single-asset hesaplamaları için kabul edilen tipler (sıralı).</summary>
    public static readonly IReadOnlyList<string> WhatIfAccepted = new[]
    {
        Grams,
        Try,
        Units,
    };

    /// <summary>DB CHECK constraint için izin verilen tüm değerler (sıralı).</summary>
    public static readonly IReadOnlyList<string> All = WhatIfAccepted;

    /// <summary>O(1) membership kontrolü.</summary>
    public static readonly IReadOnlySet<string> Lookup =
        new HashSet<string>(All, StringComparer.Ordinal);
}
