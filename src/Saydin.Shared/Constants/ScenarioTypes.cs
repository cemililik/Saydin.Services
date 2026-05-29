using System.Collections.Immutable;

namespace Saydin.Shared.Constants;

/// <summary>
/// `saved_scenarios.type` için kanonik değerler. DB CHECK constraint
/// (`chk_saved_scenarios_type`) bu listeyle birebir aynıdır; yeni bir senaryo tipi
/// eklenirken hem migration hem bu liste güncellenmelidir.
/// </summary>
public static class ScenarioTypes
{
    public const string WhatIf     = "what_if";
    public const string Comparison = "comparison";
    public const string Portfolio  = "portfolio";
    public const string Dca        = "dca";

    /// <summary>F13 follow-up: gerçek immutable koleksiyon — CHECK SQL deterministik.</summary>
    public static readonly ImmutableArray<string> All = ImmutableArray.Create(
        Comparison,
        Dca,
        Portfolio,
        WhatIf);

    /// <summary>O(1) membership kontrolü.</summary>
    public static readonly IReadOnlySet<string> Lookup =
        new HashSet<string>(All, StringComparer.Ordinal);
}
