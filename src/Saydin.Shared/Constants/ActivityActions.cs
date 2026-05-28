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

    /// <summary>
    /// F12 follow-up: HashSet yerine sıralı IReadOnlyList — `string.Join` çıktısı
    /// deterministic olur (EF Configuration CHECK SQL stabil; Add-Migration drift yok).
    /// Sıra alfabetik tutulur ki sembol eklendiğinde diff minimal kalsın.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        AssetPrice,
        AssetPriceRange,
        AssetsList,
        ConfigFetch,
        ScenarioDelete,
        ScenarioList,
        ScenarioSave,
        WhatIfCalculate,
        WhatIfCompare,
        WhatIfDca,
        WhatIfReverse,
    };

    /// <summary>
    /// O(1) membership kontrolü için ayrı HashSet. `All` ile aynı değerleri taşır.
    /// Caller'lar (Builder/ChannelActivityLogger) bunu kullanır.
    /// </summary>
    public static readonly IReadOnlySet<string> Lookup =
        new HashSet<string>(All, StringComparer.Ordinal);
}
