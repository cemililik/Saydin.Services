namespace Saydin.Shared.Constants;

/// <summary>
/// `activity_logs.action` için kanonik değerler. DB CHECK constraint
/// (`chk_activity_action`) bu listeyle birebir aynıdır; yeni bir endpoint action'ı
/// eklenirken hem migration hem bu liste güncellenmelidir.
/// </summary>
public static class ActivityActions
{
    public const string WhatIfCalculate = "what_if_calculate";
    public const string WhatIfCompare   = "what_if_compare";
    public const string WhatIfDca       = "what_if_dca";
    public const string WhatIfReverse   = "what_if_reverse";
    public const string ScenarioSave    = "scenario_save";
    public const string ScenarioDelete  = "scenario_delete";
    public const string ScenarioList    = "scenario_list";
    public const string AssetsList      = "assets_list";
    public const string AssetPrice      = "asset_price";
    public const string AssetPriceRange = "asset_price_range";
    public const string ConfigFetch     = "config_fetch";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            WhatIfCalculate, WhatIfCompare, WhatIfDca, WhatIfReverse,
            ScenarioSave, ScenarioDelete, ScenarioList,
            AssetsList, AssetPrice, AssetPriceRange, ConfigFetch,
        };
}
