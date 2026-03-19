namespace Saydin.Api.Models.Requests;

public record ReverseWhatIfRequest(
    string   AssetSymbol,
    DateOnly BuyDate,
    DateOnly? SellDate,
    decimal  TargetAmount,
    string   TargetAmountType,   // "try" | "units" | "grams"
    bool     IncludeInflation = false
);
