namespace Saydin.Api.Models.Responses;

public record ReverseWhatIfResponse(
    string   AssetSymbol,
    string   AssetDisplayName,
    DateOnly BuyDate,
    DateOnly SellDate,
    decimal  BuyPrice,
    decimal  SellPrice,
    decimal  RequiredInvestmentTry,
    decimal  UnitsAcquired,
    decimal  TargetValueTry,
    decimal  ProfitLossTry,
    decimal  ProfitLossPercent,
    bool     IsProfit,
    IReadOnlyList<PriceHistoryPoint> PriceHistory,
    decimal?  CumulativeInflationPercent,
    decimal?  RealProfitLossPercent,
    DateOnly? InflationDataAsOf,
    DateOnly? ActualBuyDate,
    DateOnly? ActualSellDate
);
