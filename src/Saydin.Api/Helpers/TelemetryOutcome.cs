namespace Saydin.Api.Helpers;

/// <summary>
/// Finansal tutar veya yüzdeyi telemetry sink'ine taşımadan düşük kardinaliteli
/// sonuç yönüne indirger.
/// </summary>
internal static class TelemetryOutcome
{
    internal static string From(decimal? profitOrReturn) => profitOrReturn switch
    {
        null => "unavailable",
        > 0m => "profit",
        < 0m => "loss",
        _ => "flat",
    };
}
