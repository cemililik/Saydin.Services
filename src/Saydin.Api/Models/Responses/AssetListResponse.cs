namespace Saydin.Api.Models.Responses;

/// <summary>
/// APIR-016: `GET /v1/assets` ve `GET /v1/assets/{symbol}/price-range` için
/// typed wrapper. Eski `Produces<object>` anonim wrapper OpenAPI şemasına yansımıyordu;
/// Flutter codegen artık typed `AssetListResponse` üretir.
/// </summary>
public record AssetListResponse(IReadOnlyList<AssetResponse> Assets);

/// <summary>APIR-016: `price-range` endpoint'i için typed yanıt.</summary>
public record PriceRangeResponse(
    string Symbol,
    string Interval,
    IReadOnlyList<Saydin.Shared.Entities.PricePoint> PricePoints);
