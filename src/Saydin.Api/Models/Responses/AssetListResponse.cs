namespace Saydin.Api.Models.Responses;

/// <summary>
/// APIR-016: `GET /v1/assets` ve `GET /v1/assets/{symbol}/price-range` için
/// typed wrapper. Eski `Produces<object>` anonim wrapper OpenAPI şemasına yansımıyordu;
/// Flutter codegen artık typed `AssetListResponse` üretir.
/// </summary>
public record AssetListResponse(IReadOnlyList<AssetResponse> Assets);

/// <summary>
/// APIR-016 / F7 follow-up: `price-range` endpoint'i için typed yanıt. Domain
/// entity (<see cref="Saydin.Shared.Entities.PricePoint"/>) sızıntısı kaldırıldı;
/// public alanlar <see cref="PricePointResponse"/>'ta tanımlıdır.
/// </summary>
public record PriceRangeResponse(
    string Symbol,
    string Interval,
    IReadOnlyList<PricePointResponse> PricePoints);

/// <summary>
/// F7 follow-up: domain `PricePoint` entity'sinden ayrı public DTO. AssetId/Asset
/// navigation gibi internal alanlar response'a sızmaz; OpenAPI şeması sade kalır.
/// Date/Close zorunlu; Open/High/Low/Volume bazı kaynaklar için <c>null</c> dönebilir
/// (TCMB ForexBuying tek değer, OXR yalnız spot).
/// </summary>
public record PricePointResponse(
    DateOnly Date,
    decimal Close,
    decimal? Open,
    decimal? High,
    decimal? Low,
    decimal? Volume);
