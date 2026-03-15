namespace Saydin.Api.Models.Responses;

public record ScenarioResponse(
    Guid Id,
    string AssetSymbol,
    string AssetDisplayName,
    DateOnly BuyDate,
    DateOnly? SellDate,
    decimal Amount,
    string AmountType,
    string? Label,
    DateTimeOffset CreatedAt
);
