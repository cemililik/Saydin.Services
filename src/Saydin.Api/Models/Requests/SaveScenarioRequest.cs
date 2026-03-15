namespace Saydin.Api.Models.Requests;

public record SaveScenarioRequest(
    string AssetSymbol,
    DateOnly BuyDate,
    DateOnly? SellDate,
    decimal Amount,
    string AmountType,
    string? Label
);
