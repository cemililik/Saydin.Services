namespace Saydin.Api.Models.Responses;

public record AppConfigResponse(
    string          Tier,
    int             DailyCalculationLimit,
    int             MaxSavedScenarios,
    AppFeatureFlags Features);

public record AppFeatureFlags(
    bool Comparison,
    bool InflationAdjustment,
    bool Share,
    int  PriceHistoryMonths);
