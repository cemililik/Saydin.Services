using System.Collections.Immutable;

namespace Saydin.Shared.Constants;

/// <summary>
/// `inflation_rates.source` için kanonik değerler. DB CHECK constraint
/// (`chk_inflation_rates_source`, migration 011) ve composite PK
/// (`period_date, source`, migration 012/F2.7-5) bu kümeyle uyumludur.
///
/// <see cref="Tuik"/> = EVDS worker'ın yazdığı gerçek TÜİK verisi.
/// <see cref="SeedApproximation"/> = migration 004 ilk seed (yaklaşık değerler).
/// Composite PK sayesinde aynı ay için her iki kaynak bir arada tutulabilir;
/// okuma yolu (Saydin.Api InflationRepository) <see cref="Tuik"/>'i tercih eder.
/// </summary>
public static class InflationSources
{
    public const string Tuik             = "tuik";
    public const string SeedApproximation = "seed-approximation";

    /// <summary>DB CHECK constraint için izin verilen tüm değerler.</summary>
    public static readonly ImmutableArray<string> All = ImmutableArray.Create(
        SeedApproximation,
        Tuik);

    /// <summary>O(1) membership kontrolü.</summary>
    public static readonly IReadOnlySet<string> Lookup =
        new HashSet<string>(All, StringComparer.Ordinal);
}
