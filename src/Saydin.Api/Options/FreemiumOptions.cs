namespace Saydin.Api.Options;

public sealed class FreemiumOptions
{
    public const string SectionName = "Freemium";

    /// <summary>Ücretsiz kullanıcının günlük başarılı hesaplama hakkı.</summary>
    public int DailyCalculationLimit { get; init; } = 10;

    /// <summary>Ücretsiz kullanıcının kaydedebileceği maksimum senaryo sayısı.</summary>
    public int ScenarioLimit { get; init; } = 5;
}
