namespace Saydin.Api.Models.Requests;

public record WhatIfRequest(
    string AssetSymbol,
    DateOnly BuyDate,
    DateOnly? SellDate,
    decimal Amount,
    string AmountType  // "try" | "units" | "grams"
);
