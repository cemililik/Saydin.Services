namespace Saydin.Api.Options;

public sealed class PlanOptions
{
    public const string SectionName = "Plans";

    public TierOptions Free    { get; init; } = new();
    public TierOptions Premium { get; init; } = new();

    /// <summary>Kullanıcı tier'ına göre plan seçeneklerini döner. Bilinmeyen tier → Free.</summary>
    public TierOptions GetTierOptions(string? tier) =>
        tier?.Equals("premium", StringComparison.OrdinalIgnoreCase) == true ? Premium : Free;
}

public sealed class TierOptions
{
    /// <summary>Günlük hesaplama limiti. 0 = sınırsız.</summary>
    public int DailyCalculationLimit { get; init; } = 20;

    /// <summary>Kaydedilebilecek maksimum senaryo sayısı. 0 = sınırsız.</summary>
    public int MaxSavedScenarios { get; init; } = 10;

    public FeatureOptions Features { get; init; } = new();
}

public sealed class FeatureOptions
{
    public bool Comparison          { get; init; } = true;
    public bool InflationAdjustment { get; init; } = true;
    public bool Share               { get; init; } = true;
    public bool Dca                 { get; init; } = true;

    /// <summary>Erişilebilir fiyat geçmişi (ay). 0 = tüm geçmiş.</summary>
    public int PriceHistoryMonths { get; init; } = 12;
}
