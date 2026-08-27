using System.Collections.Immutable;

namespace Saydin.Shared.Constants;

/// <summary>
/// `activity_logs.action` için kanonik değerler. DB CHECK constraint
/// (`enforce_activity_action_allowlist`) bu listeyle birebir aynıdır; yeni bir endpoint action'ı
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
    public const string InstallationRegister = "installation_register";
    public const string InstallationRotationBegin = "installation_rotation_begin";
    public const string InstallationRotationCommit = "installation_rotation_commit";
    public const string InstallationRevoke = "installation_revoke";

    /// <summary>
    /// F12 follow-up: gerçek immutable koleksiyon. Sıra alfabetik; caller `(string[])All`
    /// downcast ile mutate edemez (önceki `IReadOnlyList<string> = new[]{...}` array
    /// backing'i downcast'la elemana yazma yapabiliyordu).
    /// </summary>
    public static readonly ImmutableArray<string> All = ImmutableArray.Create(
        AssetPrice,
        AssetPriceRange,
        AssetsList,
        ConfigFetch,
        InstallationRegister,
        InstallationRevoke,
        InstallationRotationBegin,
        InstallationRotationCommit,
        ScenarioDelete,
        ScenarioList,
        ScenarioSave,
        WhatIfCalculate,
        WhatIfCompare,
        WhatIfDca,
        WhatIfReverse);

    /// <summary>O(1) membership kontrolü için ayrı HashSet (immutable snapshot üzerinden).</summary>
    public static readonly IReadOnlySet<string> Lookup =
        new HashSet<string>(All, StringComparer.Ordinal);
}
