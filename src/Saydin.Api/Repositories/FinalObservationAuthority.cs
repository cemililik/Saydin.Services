using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

/// <summary>
/// API consumer trust boundary for migration 020 observations. The database accepts
/// all-null legacy tuples during the expand/backfill phase; product reads must opt in
/// only to complete, final, provider-specific authority tuples.
/// </summary>
internal static class FinalObservationAuthority
{
    internal static IQueryable<PricePoint> WhereCompleteFinalAuthority(
        this IQueryable<PricePoint> query) =>
        query.Where(point =>
            point.IsFinal == true
            && point.ProviderSource != null
            && point.SourceObservationId != null
            && point.AsOfAt != null
            && point.PriceKind != null
            && point.ObservationSha256 != null
            && point.ObservationSha256.Length == ObservationAuthorityLimits.Sha256Bytes
            && point.AuthorityContractVersion > 0
            && point.SourceRaw != null
            && point.ProviderSource == point.Asset.Source
            && ((point.ProviderSource == ProviderSources.Tcmb
                 && point.PriceKind == ObservationPriceKinds.OfficialReference)
                || (point.ProviderSource == ProviderSources.CoinGecko
                    && point.PriceKind == ObservationPriceKinds.DailyUtcReference)
                || (point.ProviderSource == ProviderSources.OpenExchangeRates
                    && point.PriceKind == ObservationPriceKinds.DailyReference)
                || (point.ProviderSource == ProviderSources.TwelveData
                    && point.PriceKind == ObservationPriceKinds.DailyClose)));

    internal static IQueryable<InflationRate> WhereCompleteFinalAuthority(
        this IQueryable<InflationRate> query) =>
        query.Where(rate =>
            rate.Source == InflationSources.Tuik
            && rate.ProviderSource == ProviderSources.Evds
            && rate.PriceKind == ObservationPriceKinds.CpiIndex
            && rate.IsFinal == true
            && rate.SourceObservationId != null
            && rate.AsOfAt != null
            && rate.ObservationSha256 != null
            && rate.ObservationSha256.Length == ObservationAuthorityLimits.Sha256Bytes
            && rate.AuthorityContractVersion > 0
            && rate.SourceRaw != null);

    internal static bool IsCompleteFinal(PricePoint point) =>
        point.IsFinal == true
        && !string.IsNullOrEmpty(point.ProviderSource)
        && !string.IsNullOrEmpty(point.SourceObservationId)
        && point.AsOfAt.HasValue
        && !string.IsNullOrEmpty(point.PriceKind)
        && point.ObservationSha256 is { Length: ObservationAuthorityLimits.Sha256Bytes }
        && point.AuthorityContractVersion > 0
        && point.SourceRaw is not null
        && IsSupportedPricePair(point.ProviderSource, point.PriceKind);

    internal static ObservationAuthorityValue ToValue(PricePoint point)
    {
        if (!IsCompleteFinal(point))
            throw new InvalidOperationException("price_authority_not_final");

        return new ObservationAuthorityValue(
            point.ProviderSource!, point.PriceKind!, point.AsOfAt!.Value,
            point.AuthorityContractVersion!.Value);
    }

    internal static ObservationAuthorityValue ToValue(InflationRate rate)
    {
        if (rate.Source != InflationSources.Tuik
            || rate.ProviderSource != ProviderSources.Evds
            || rate.PriceKind != ObservationPriceKinds.CpiIndex
            || rate.IsFinal != true
            || string.IsNullOrEmpty(rate.SourceObservationId)
            || !rate.AsOfAt.HasValue
            || rate.ObservationSha256 is not { Length: ObservationAuthorityLimits.Sha256Bytes }
            || rate.AuthorityContractVersion is not > 0
            || rate.SourceRaw is null)
        {
            throw new InvalidOperationException("inflation_authority_not_final");
        }

        return new ObservationAuthorityValue(
            rate.ProviderSource, rate.PriceKind, rate.AsOfAt.Value,
            rate.AuthorityContractVersion.Value);
    }

    private static bool IsSupportedPricePair(string provider, string kind) =>
        (provider, kind) switch
        {
            (ProviderSources.Tcmb, ObservationPriceKinds.OfficialReference) => true,
            (ProviderSources.CoinGecko, ObservationPriceKinds.DailyUtcReference) => true,
            (ProviderSources.OpenExchangeRates, ObservationPriceKinds.DailyReference) => true,
            (ProviderSources.TwelveData, ObservationPriceKinds.DailyClose) => true,
            _ => false,
        };
}

public sealed record ObservationAuthorityValue(
    string ProviderSource,
    string PriceKind,
    DateTimeOffset AsOfAt,
    int AuthorityContractVersion);

public sealed record InflationIndexObservation(
    DateOnly PeriodDate,
    decimal IndexValue,
    ObservationAuthorityValue Authority);
