namespace Saydin.Api.Services;

internal static class ScenarioLimits
{
    internal const int SystemSaveHardLimit = 100;
    internal const int LegacyListHardLimit = 100;
    internal const int DefaultPageSize = 20;
    internal const int MaxPageSize = 50;

    public static int GetEffectiveSaveLimit(int configuredLimit) =>
        configuredLimit <= 0
            ? SystemSaveHardLimit
            : Math.Min(configuredLimit, SystemSaveHardLimit);
}
