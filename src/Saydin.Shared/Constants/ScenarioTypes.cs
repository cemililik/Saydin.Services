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

    /// <summary>Tüm geçerli değerlerin sırasız kümesi (case-sensitive eşleşme).</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { WhatIf, Comparison, Portfolio, Dca };
}
