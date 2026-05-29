using System.Collections.Immutable;

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

    /// <summary>WhatIf single-asset hesaplamaları için kabul edilen tipler.</summary>
    public static readonly ImmutableArray<string> WhatIfAccepted = ImmutableArray.Create(
        Grams,
        Try,
        Units);

    /// <summary>
    /// DCA hesaplamaları için kabul edilen tipler. DCA periyodik yatırım yalnızca
    /// TL bazında anlamlıdır (her periyotta sabit ₺ tutarı yatırılır) — units/grams
    /// için periyodik miktar semantiği yoktur. F3.1-2: önceki <c>amountType is not "try"</c>
    /// literal kontrolü bu kümeye indirgendi.
    /// </summary>
    public static readonly ImmutableArray<string> DcaAccepted = ImmutableArray.Create(Try);

    /// <summary>DB CHECK constraint için izin verilen tüm değerler.</summary>
    public static readonly ImmutableArray<string> All = WhatIfAccepted;

    /// <summary>O(1) membership kontrolü.</summary>
    public static readonly IReadOnlySet<string> Lookup =
        new HashSet<string>(All, StringComparer.Ordinal);
}
