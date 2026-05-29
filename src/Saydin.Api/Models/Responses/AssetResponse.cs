namespace Saydin.Api.Models.Responses;

/// <summary>
/// F2.3-7 ([C-C-32]): `Category` artık string olarak döner — Shared'daki
/// <c>AssetCategory</c> enum'u DTO yüzeyinden sızdırılmaz. JSON yanıtında
/// snake_case değerler (<c>currency</c>, <c>precious_metal</c>, <c>stock</c>,
/// <c>crypto</c>) korunur.
/// </summary>
public record AssetResponse(
    string Symbol,
    string DisplayName,
    string Category,
    DateOnly? FirstPriceDate,
    DateOnly? LastPriceDate
);
