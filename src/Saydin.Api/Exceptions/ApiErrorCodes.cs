namespace Saydin.Api.Exceptions;

/// <summary>
/// EC-3: Lokale ve media-type'tan bağımsız, makine-okunur <b>kararlı</b> hata kodları.
/// Her hata yanıtının <c>ProblemDetails.Extensions["code"]</c> alanında döner.
///
/// <para>
/// <b>Neden gerekli?</b> Tek kararlı ayraç şu ana dek <c>type</c> URI slug'ıydı; ancak
/// <c>title</c>/<c>detail</c> <c>Accept-Language</c>'e göre lokalize olduğundan anahtar
/// olamaz ve <c>type</c> URI'si ileride değişebilir. İstemci bu <c>code</c>'a göre
/// dallanır → <c>type</c> değişse bile kırılmaz.
/// </para>
///
/// <para>
/// Değerler <c>type</c> URI slug'ıyla birebir eşleşir (kebab-case slug → snake_case code,
/// ör. <c>.../errors/feature-disabled</c> → <c>feature_disabled</c>). Bu sınıf + meta repo
/// <c>docs/architecture/api-contract.md</c> hata taksonomisi tablosu birlikte kaynak doğrusudur.
/// </para>
/// </summary>
internal static class ApiErrorCodes
{
    public const string Validation            = "validation";
    public const string FeatureDisabled       = "feature_disabled";
    public const string PriceNotFound         = "price_not_found";
    public const string AssetNotFound         = "asset_not_found";
    public const string ScenarioNotFound      = "scenario_not_found";
    public const string ScenarioLimitExceeded = "scenario_limit_exceeded";
    public const string DailyLimitExceeded    = "daily_limit_exceeded";
    public const string ExternalApi           = "external_api";
    public const string InternalError         = "internal_error";
    public const string RateLimited           = "rate_limited";
    public const string PayloadTooLarge       = "payload_too_large";
    public const string UnsupportedMediaType  = "unsupported_media_type";
    public const string InvalidInstallationCredential = "invalid_installation_credential";
}
