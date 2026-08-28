namespace Saydin.Api.Models.Responses;

public record DcaPurchase(
    DateOnly Date,
    decimal  Price,
    decimal  UnitsAcquired,
    decimal  CumulativeUnits,
    decimal  CumulativeCostTry,
    decimal  CumulativeValueTry
);

public record DcaChartPoint(
    DateOnly Date,
    decimal  CumulativeCost,
    decimal  CumulativeValue
);

public record DcaResponse(
    string   AssetSymbol,
    string   AssetDisplayName,
    DateOnly StartDate,
    DateOnly EndDate,
    string   Period,
    decimal  PeriodicAmount,
    int      TotalPurchases,
    decimal  TotalInvestedTry,
    decimal  CurrentValueTry,
    decimal  ProfitLossTry,
    decimal  ProfitLossPercent,
    bool     IsProfit,
    decimal  AverageCostPerUnit,
    decimal  TotalUnitsAcquired,
    decimal  CurrentUnitPrice,
    // Enflasyon düzeltmesi — IncludeInflation = false ise null
    decimal?  CumulativeInflationPercent,
    decimal?  RealProfitLossPercent,
    // WhatIf ile aynı legacy as-of semantiği: terminal CPI hedef aydan gerideyse
    // gerçekten kullanılan final CPI ayı, exact hedef ay kullanıldıysa null.
    DateOnly? InflationDataAsOf,
    IReadOnlyList<DcaPurchase>   Purchases,
    IReadOnlyList<DcaChartPoint> ChartData,
    // Additive reel-getiri alanları. Tutarlar terminal tarihten ileri olmayan son
    // final CPI deflatörüyle ve yalnız response sınırında 2 haneye yuvarlanır.
    decimal? InflationAdjustedInvestedTry = null,
    decimal? RealProfitLossTry = null,
    string?  RealReturnMethod = null,
    DateOnly? InflationTerminalMonth = null,
    CalculationDataResponse? Data = null,
    IReadOnlyList<DateOnly>? SkippedPurchaseDates = null
);
