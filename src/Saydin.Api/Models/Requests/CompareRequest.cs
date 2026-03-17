namespace Saydin.Api.Models.Requests;

public record CompareRequest(
    List<string> AssetSymbols,   // 2-5 sembol
    DateOnly     BuyDate,
    DateOnly?    SellDate,
    decimal      Amount,
    string       AmountType,     // "try" | "units" | "grams"
    bool         IncludeInflation = false
);
