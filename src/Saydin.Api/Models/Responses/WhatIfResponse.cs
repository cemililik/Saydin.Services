namespace Saydin.Api.Models.Responses;

public record WhatIfResponse(
    string AssetSymbol,
    string AssetDisplayName,
    DateOnly BuyDate,
    DateOnly SellDate,
    decimal BuyPrice,
    decimal SellPrice,
    decimal UnitsAcquired,
    decimal InitialValueTry,
    decimal FinalValueTry,
    decimal ProfitLossTry,
    decimal ProfitLossPercent,
    bool IsProfit
);
