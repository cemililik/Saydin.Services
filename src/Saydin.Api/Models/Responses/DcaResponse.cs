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
    // Legacy as-of alanı: complete exact hesapta terminal CPI ayı, veri eksikse null.
    DateOnly? InflationDataAsOf,
    IReadOnlyList<DcaPurchase>   Purchases,
    IReadOnlyList<DcaChartPoint> ChartData,
    // Additive API-04 alanları. Tutarlar terminal fiyat gününün TÜFE ayındaki TL
    // satın alma gücüyle ve yalnız final sınırında 2 haneye yuvarlanır.
    decimal? InflationAdjustedInvestedTry = null,
    decimal? RealProfitLossTry = null,
    string?  RealReturnMethod = null,
    DateOnly? InflationTerminalMonth = null,
    CalculationDataResponse? Data = null
);
