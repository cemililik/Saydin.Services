using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

/// <summary>
/// Current-namespace calculation cache entries are still untrusted input. These
/// request-bound envelopes prevent a complete response stored under the wrong key
/// from crossing request identity, amount, date or inflation boundaries.
/// </summary>
internal sealed record WhatIfCacheEntry(
    string Symbol,
    DateOnly BuyDate,
    DateOnly SellDate,
    decimal Amount,
    string AmountType,
    bool IncludeInflation,
    string Language,
    WhatIfResponse? Response,
    long CatalogRevision = 0,
    string CatalogHash = "")
{
    internal static WhatIfCacheEntry Create(
        string symbol, DateOnly buyDate, DateOnly sellDate, decimal amount,
        string amountType, bool includeInflation, string language,
        WhatIfResponse response)
        => Create(
            symbol, buyDate, sellDate, amount, amountType,
            includeInflation, language, response, null);

    internal static WhatIfCacheEntry Create(
        string symbol, DateOnly buyDate, DateOnly sellDate, decimal amount,
        string amountType, bool includeInflation, string language,
        WhatIfResponse response,
        Repositories.AssetCatalogVersion? catalog)
    {
        var entry = new WhatIfCacheEntry(
            symbol, buyDate, sellDate, amount, amountType,
            includeInflation, language, response,
            catalog?.Revision ?? 0,
            catalog is null ? string.Empty : Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant());
        if (!entry.IsValid(
                symbol, buyDate, sellDate, amount, amountType,
                includeInflation, language))
            throw new InvalidOperationException("whatif_cache_contract_invalid");
        return entry;
    }

    internal bool IsValid(
        string symbol, DateOnly buyDate, DateOnly sellDate, decimal amount,
        string amountType, bool includeInflation, string language,
        Repositories.AssetCatalogVersion catalog) =>
        IsValid(symbol, buyDate, sellDate, amount, amountType, includeInflation, language)
        && CatalogCacheContract.Matches(CatalogRevision, CatalogHash, catalog);

    internal bool IsValid(
        string symbol, DateOnly buyDate, DateOnly sellDate, decimal amount,
        string amountType, bool includeInflation, string language) =>
        string.Equals(Symbol, symbol, StringComparison.Ordinal)
        && BuyDate == buyDate
        && SellDate == sellDate
        && Amount == amount
        && string.Equals(AmountType, amountType, StringComparison.Ordinal)
        && IncludeInflation == includeInflation
        && CalculationCacheStampContract.IsLanguage(Language)
        && CalculationCacheStampContract.IsLanguage(language)
        && string.Equals(Language, language, StringComparison.Ordinal)
        && Response is not null
        && string.Equals(Response.AssetSymbol, symbol, StringComparison.Ordinal)
        && Response.BuyDate == buyDate
        && Response.SellDate == sellDate
        && CalculationCacheContract.IsComplete(Response.Data, includeInflation);
}

internal sealed record ReverseWhatIfCacheEntry(
    string Symbol,
    DateOnly BuyDate,
    DateOnly SellDate,
    decimal TargetAmount,
    string TargetAmountType,
    bool IncludeInflation,
    string Language,
    ReverseWhatIfResponse? Response,
    long CatalogRevision = 0,
    string CatalogHash = "")
{
    internal static ReverseWhatIfCacheEntry Create(
        string symbol, DateOnly buyDate, DateOnly sellDate, decimal targetAmount,
        string targetAmountType, bool includeInflation, string language,
        ReverseWhatIfResponse response)
        => Create(
            symbol, buyDate, sellDate, targetAmount, targetAmountType,
            includeInflation, language, response, null);

    internal static ReverseWhatIfCacheEntry Create(
        string symbol, DateOnly buyDate, DateOnly sellDate, decimal targetAmount,
        string targetAmountType, bool includeInflation, string language,
        ReverseWhatIfResponse response,
        Repositories.AssetCatalogVersion? catalog)
    {
        var entry = new ReverseWhatIfCacheEntry(
            symbol, buyDate, sellDate, targetAmount, targetAmountType,
            includeInflation, language, response,
            catalog?.Revision ?? 0,
            catalog is null ? string.Empty : Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant());
        if (!entry.IsValid(
                symbol, buyDate, sellDate, targetAmount, targetAmountType,
                includeInflation, language))
            throw new InvalidOperationException("reverse_whatif_cache_contract_invalid");
        return entry;
    }

    internal bool IsValid(
        string symbol, DateOnly buyDate, DateOnly sellDate, decimal targetAmount,
        string targetAmountType, bool includeInflation, string language,
        Repositories.AssetCatalogVersion catalog) =>
        IsValid(
            symbol, buyDate, sellDate, targetAmount, targetAmountType,
            includeInflation, language)
        && CatalogCacheContract.Matches(CatalogRevision, CatalogHash, catalog);

    internal bool IsValid(
        string symbol, DateOnly buyDate, DateOnly sellDate, decimal targetAmount,
        string targetAmountType, bool includeInflation, string language) =>
        string.Equals(Symbol, symbol, StringComparison.Ordinal)
        && BuyDate == buyDate
        && SellDate == sellDate
        && TargetAmount == targetAmount
        && string.Equals(TargetAmountType, targetAmountType, StringComparison.Ordinal)
        && IncludeInflation == includeInflation
        && CalculationCacheStampContract.IsLanguage(Language)
        && CalculationCacheStampContract.IsLanguage(language)
        && string.Equals(Language, language, StringComparison.Ordinal)
        && Response is not null
        && string.Equals(Response.AssetSymbol, symbol, StringComparison.Ordinal)
        && Response.BuyDate == buyDate
        && Response.SellDate == sellDate
        && CalculationCacheContract.IsComplete(Response.Data, includeInflation);
}

internal sealed record DcaCacheEntry(
    string Symbol,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal PeriodicAmount,
    string Period,
    string AmountType,
    bool IncludeInflation,
    string Language,
    DcaResponse? Response,
    long CatalogRevision = 0,
    string CatalogHash = "")
{
    internal static DcaCacheEntry Create(
        string symbol, DateOnly startDate, DateOnly endDate, decimal periodicAmount,
        string period, string amountType, bool includeInflation, string language,
        DcaResponse response)
        => Create(
            symbol, startDate, endDate, periodicAmount, period, amountType,
            includeInflation, language, response, null);

    internal static DcaCacheEntry Create(
        string symbol, DateOnly startDate, DateOnly endDate, decimal periodicAmount,
        string period, string amountType, bool includeInflation, string language,
        DcaResponse response,
        Repositories.AssetCatalogVersion? catalog)
    {
        var entry = new DcaCacheEntry(
            symbol, startDate, endDate, periodicAmount, period, amountType,
            includeInflation, language, response,
            catalog?.Revision ?? 0,
            catalog is null ? string.Empty : Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant());
        if (!entry.IsValid(
                symbol, startDate, endDate, periodicAmount, period, amountType,
                includeInflation, language))
            throw new InvalidOperationException("dca_cache_contract_invalid");
        return entry;
    }

    internal bool IsValid(
        string symbol, DateOnly startDate, DateOnly endDate, decimal periodicAmount,
        string period, string amountType, bool includeInflation, string language,
        Repositories.AssetCatalogVersion catalog) =>
        IsValid(
            symbol, startDate, endDate, periodicAmount, period, amountType,
            includeInflation, language)
        && CatalogCacheContract.Matches(CatalogRevision, CatalogHash, catalog);

    internal bool IsValid(
        string symbol, DateOnly startDate, DateOnly endDate, decimal periodicAmount,
        string period, string amountType, bool includeInflation, string language) =>
        string.Equals(Symbol, symbol, StringComparison.Ordinal)
        && StartDate == startDate
        && EndDate == endDate
        && PeriodicAmount == periodicAmount
        && string.Equals(Period, period, StringComparison.Ordinal)
        && string.Equals(AmountType, amountType, StringComparison.Ordinal)
        && IncludeInflation == includeInflation
        && CalculationCacheStampContract.IsLanguage(Language)
        && CalculationCacheStampContract.IsLanguage(language)
        && string.Equals(Language, language, StringComparison.Ordinal)
        && Response is not null
        && string.Equals(Response.AssetSymbol, symbol, StringComparison.Ordinal)
        && Response.StartDate == startDate
        && Response.EndDate == endDate
        && Response.PeriodicAmount == periodicAmount
        && string.Equals(Response.Period, period, StringComparison.Ordinal)
        && CalculationCacheContract.IsComplete(Response.Data, includeInflation);
}

internal static class CatalogCacheContract
{
    internal static bool Matches(
        long revision,
        string? hash,
        Repositories.AssetCatalogVersion catalog) =>
        catalog.IsValid
        && CalculationCacheStampContract.IsCatalogHash(hash)
        && revision == catalog.Revision
        && string.Equals(
            hash,
            Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant(),
            StringComparison.Ordinal);
}

internal static class CalculationCacheStampContract
{
    internal static bool IsLanguage(string? language) =>
        language is { Length: 2 }
        && language.All(static character => character is >= 'a' and <= 'z');

    internal static bool IsCatalogHash(string? hash) =>
        hash is { Length: 64 }
        && hash.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class CalculationCacheContract
{
    internal static bool IsComplete(
        CalculationDataResponse? data,
        bool includeInflation)
    {
        if (data is not { DataStatus: AuthorityDataStatuses.Complete }
            || data.Warnings is null
            || data.Warnings.Count != 0
            || data.PriceBasis is null
            || data.PriceBasis.DataStatus != AuthorityDataStatuses.Final
            || data.PriceBasis.ObservationCount <= 0
            || data.InflationBasis is null)
            return false;

        return includeInflation
            ? data.InflationBasis.DataStatus == AuthorityDataStatuses.Final
              && data.InflationBasis.ObservationCount > 0
            : data.InflationBasis.DataStatus == AuthorityDataStatuses.NotRequested
              && data.InflationBasis.ObservationCount == 0;
    }
}
