using Saydin.Shared.Entities;

namespace Saydin.Api.Models.Responses;

public record AssetResponse(
    string Symbol,
    string DisplayName,
    AssetCategory Category,
    DateOnly? FirstPriceDate,
    DateOnly? LastPriceDate
);
