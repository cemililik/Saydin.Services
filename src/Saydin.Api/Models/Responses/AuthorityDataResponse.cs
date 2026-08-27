using Saydin.Api.Repositories;
using Saydin.Shared.Entities;

namespace Saydin.Api.Models.Responses;

public static class AuthorityDataStatuses
{
    public const string Complete = "complete";
    public const string Degraded = "degraded";
    public const string Final = "final";
    public const string Unavailable = "unavailable";
    public const string NotRequested = "not_requested";
}

public static class AuthorityDataWarnings
{
    public const string PriceHistoryUnavailable = "price_history_unavailable";
    public const string PurchasePriceUnavailable = "purchase_price_unavailable";
    public const string InflationUnavailable = "inflation_unavailable";
    public const string InflationIncomplete = "inflation_incomplete";
}

/// <summary>
/// Public, bounded authority basis. Observation IDs, hashes and normalized evidence
/// deliberately remain internal.
/// </summary>
public sealed record ObservationBasisResponse(
    string DataStatus,
    string ProviderSource,
    string PriceKind,
    DateTimeOffset AsOfAt,
    int AuthorityContractVersion);

/// <summary>
/// Compact basis for calculations that consume more than one observation.
/// </summary>
public sealed record ObservationBasisSummaryResponse(
    string DataStatus,
    IReadOnlyList<string> ProviderSources,
    IReadOnlyList<string> PriceKinds,
    int ObservationCount,
    int? MinAuthorityContractVersion,
    int? MaxAuthorityContractVersion,
    DateTimeOffset? AsOfFrom,
    DateTimeOffset? AsOfThrough);

public sealed record CalculationDataResponse(
    string DataStatus,
    IReadOnlyList<string> Warnings,
    ObservationBasisSummaryResponse PriceBasis,
    ObservationBasisSummaryResponse InflationBasis);

internal static class AuthorityDataResponseFactory
{
    internal static ObservationBasisResponse Exact(ObservationAuthorityValue value) =>
        new(AuthorityDataStatuses.Final, value.ProviderSource, value.PriceKind,
            value.AsOfAt, value.AuthorityContractVersion);

    internal static ObservationBasisSummaryResponse FinalSummary(
        IEnumerable<ObservationAuthorityValue> values)
    {
        var providers = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        int? minVersion = null;
        int? maxVersion = null;
        DateTimeOffset? asOfFrom = null;
        DateTimeOffset? asOfThrough = null;

        foreach (var value in values)
        {
            if (!IsPublicProvider(value.ProviderSource)
                || !IsPublicKind(value.PriceKind)
                || value.AuthorityContractVersion <= 0)
            {
                throw new InvalidOperationException("authority_summary_value_invalid");
            }

            checked { count++; }
            providers.Add(value.ProviderSource);
            kinds.Add(value.PriceKind);
            minVersion = !minVersion.HasValue
                ? value.AuthorityContractVersion
                : Math.Min(minVersion.Value, value.AuthorityContractVersion);
            maxVersion = !maxVersion.HasValue
                ? value.AuthorityContractVersion
                : Math.Max(maxVersion.Value, value.AuthorityContractVersion);
            asOfFrom = !asOfFrom.HasValue || value.AsOfAt < asOfFrom.Value
                ? value.AsOfAt
                : asOfFrom;
            asOfThrough = !asOfThrough.HasValue || value.AsOfAt > asOfThrough.Value
                ? value.AsOfAt
                : asOfThrough;
        }

        if (count == 0)
            throw new InvalidOperationException("final_authority_summary_empty");

        return new ObservationBasisSummaryResponse(
            AuthorityDataStatuses.Final,
            providers.Order(StringComparer.Ordinal).ToArray(),
            kinds.Order(StringComparer.Ordinal).ToArray(),
            count,
            minVersion,
            maxVersion,
            asOfFrom,
            asOfThrough);
    }

    internal static ObservationBasisSummaryResponse Empty(string status) =>
        new(status, Array.Empty<string>(), Array.Empty<string>(), 0, null, null, null, null);

    internal static CalculationDataResponse Calculation(
        IEnumerable<ObservationAuthorityValue> prices,
        IEnumerable<ObservationAuthorityValue> inflation,
        bool inflationRequested,
        IReadOnlyCollection<string> warnings)
    {
        var inflationSnapshot = inflation.Distinct().ToArray();
        var inflationBasis = inflationSnapshot.Length > 0
            ? FinalSummary(inflationSnapshot)
            : Empty(inflationRequested
                ? AuthorityDataStatuses.Unavailable
                : AuthorityDataStatuses.NotRequested);

        return new CalculationDataResponse(
            warnings.Count == 0 ? AuthorityDataStatuses.Complete : AuthorityDataStatuses.Degraded,
            warnings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            FinalSummary(prices),
            inflationBasis);
    }

    private static bool IsPublicProvider(string provider) => provider is
        ProviderSources.Tcmb
        or ProviderSources.CoinGecko
        or ProviderSources.OpenExchangeRates
        or ProviderSources.TwelveData
        or ProviderSources.Evds;

    private static bool IsPublicKind(string kind) => kind is
        ObservationPriceKinds.OfficialReference
        or ObservationPriceKinds.DailyUtcReference
        or ObservationPriceKinds.DailyReference
        or ObservationPriceKinds.DailyClose
        or ObservationPriceKinds.CpiIndex;
}
