using System.ComponentModel.DataAnnotations;
using Saydin.Shared.Constants;

namespace Saydin.Api.Options;

public sealed class PlanOptions : IValidatableObject
{
    public const string SectionName = "Plans";

    public TierOptions Free    { get; init; } = new();
    public TierOptions Premium { get; init; } = new();

    /// <summary>Kullanıcı tier'ına göre plan seçeneklerini döner. Bilinmeyen tier → Free.</summary>
    public TierOptions GetTierOptions(string? tier) =>
        string.Equals(tier, UserTiers.Premium, StringComparison.OrdinalIgnoreCase) ? Premium : Free;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // SVCR-008: PriceHistoryMonths < 0 sızıntısı (sınırsız anlamına gelirdi) artık
        // startup'ta fail-fast verir. Range data annotation tier'lara cascade etmediği
        // için manuel validation yapılır.
        foreach (var result in Validate(Free,    nameof(Free))) yield return result;
        foreach (var result in Validate(Premium, nameof(Premium))) yield return result;
    }

    private static IEnumerable<ValidationResult> Validate(TierOptions tier, string memberName)
    {
        if (tier.DailyCalculationLimit < 0)
            yield return new ValidationResult($"{memberName}.{nameof(TierOptions.DailyCalculationLimit)} negatif olamaz", [memberName]);
        if (tier.DailyAssetQueryLimit < 0)
            yield return new ValidationResult($"{memberName}.{nameof(TierOptions.DailyAssetQueryLimit)} negatif olamaz", [memberName]);
        if (tier.MaxSavedScenarios < 0)
            yield return new ValidationResult($"{memberName}.{nameof(TierOptions.MaxSavedScenarios)} negatif olamaz", [memberName]);
        if (tier.Features.PriceHistoryMonths < 0)
            yield return new ValidationResult($"{memberName}.Features.{nameof(FeatureOptions.PriceHistoryMonths)} negatif olamaz", [memberName]);
    }
}

public sealed class TierOptions
{
    /// <summary>Günlük hesaplama limiti. 0 = sınırsız.</summary>
    [Range(0, int.MaxValue)]
    public int DailyCalculationLimit { get; init; } = 20;

    /// <summary>Günlük asset listeleme + fiyat sorgulama limiti. 0 = sınırsız.</summary>
    [Range(0, int.MaxValue)]
    public int DailyAssetQueryLimit { get; init; } = 500;

    /// <summary>
    /// Planın kaydedilmiş senaryo limiti. 0 = plan ek limiti yok; yine de API'nin
    /// sistem hard cap'i uygulanır ve AppConfig effective değeri döndürür.
    /// </summary>
    [Range(0, int.MaxValue)]
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
    [Range(0, int.MaxValue)]
    public int PriceHistoryMonths { get; init; } = 12;
}
