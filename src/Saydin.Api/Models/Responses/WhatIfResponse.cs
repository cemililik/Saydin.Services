namespace Saydin.Api.Models.Responses;

public record PriceHistoryPoint(DateOnly Date, decimal Price);

public record WhatIfResponse(
    string   AssetSymbol,
    string   AssetDisplayName,
    DateOnly BuyDate,
    DateOnly SellDate,
    decimal  BuyPrice,
    decimal  SellPrice,
    decimal  UnitsAcquired,
    decimal  InitialValueTry,
    decimal  FinalValueTry,
    decimal  ProfitLossTry,
    decimal  ProfitLossPercent,
    bool     IsProfit,
    IReadOnlyList<PriceHistoryPoint> PriceHistory,
    // Enflasyon düzeltmesi — IncludeInflation = false ise null
    decimal?  CumulativeInflationPercent,
    decimal?  RealProfitLossPercent,
    // TÜİK yayın gecikmesi durumunda kullanılan en son endeks tarihi
    DateOnly? InflationDataAsOf,
    // Haftasonu/tatil: kullanıcının seçtiği tarih yerine işlem gören en yakın gün
    DateOnly? ActualBuyDate,
    DateOnly? ActualSellDate
);
