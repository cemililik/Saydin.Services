using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

/// <summary>
/// Request-bound cache values. A cache namespace separates releases; these envelopes
/// additionally prevent a valid-looking value written under the wrong current key from
/// crossing the final-authority read boundary.
/// </summary>
internal sealed record PriceCacheEntry(
    string Symbol,
    string AssetSource,
    Guid AssetId,
    DateOnly RequestedDate,
    int? MaxDays,
    PricePoint? Point,
    long CatalogRevision = 0,
    string CatalogHash = "")
{
    internal static PriceCacheEntry Exact(
        AssetReadIdentity asset,
        DateOnly date,
        PricePoint point) => Create(asset, date, null, point);

    internal static PriceCacheEntry Exact(
        AssetReadIdentity asset,
        DateOnly date,
        PricePoint point,
        AssetCatalogVersion catalog) => Create(asset, date, null, point, catalog);

    internal static PriceCacheEntry Nearest(
        AssetReadIdentity asset,
        DateOnly date,
        int maxDays,
        PricePoint point) => Create(asset, date, maxDays, point);

    internal static PriceCacheEntry Nearest(
        AssetReadIdentity asset,
        DateOnly date,
        int maxDays,
        PricePoint point,
        AssetCatalogVersion catalog) => Create(asset, date, maxDays, point, catalog);

    internal bool IsValidExact(AssetReadIdentity asset, DateOnly date) =>
        MatchesIdentity(asset)
        && Point is not null
        && MaxDays is null
        && RequestedDate == date
        && Point.PriceDate == date;

    internal bool IsValidExact(
        AssetReadIdentity asset,
        DateOnly date,
        AssetCatalogVersion catalog) =>
        IsValidExact(asset, date) && MatchesCatalog(catalog);

    internal bool IsValidNearest(AssetReadIdentity asset, DateOnly date, int maxDays) =>
        MatchesIdentity(asset)
        && Point is not null
        && RequestedDate == date
        && MaxDays == maxDays
        && Point.PriceDate >= date.AddDays(-maxDays)
        && Point.PriceDate <= date.AddDays(maxDays);

    internal bool IsValidNearest(
        AssetReadIdentity asset,
        DateOnly date,
        int maxDays,
        AssetCatalogVersion catalog) =>
        IsValidNearest(asset, date, maxDays) && MatchesCatalog(catalog);

    private static PriceCacheEntry Create(
        AssetReadIdentity asset,
        DateOnly requestedDate,
        int? maxDays,
        PricePoint point,
        AssetCatalogVersion? catalog = null)
    {
        if (!FinalObservationAuthority.IsCompleteFinal(point)
            || string.IsNullOrWhiteSpace(point.ProviderSource)
            || point.AssetId == Guid.Empty)
        {
            throw new InvalidOperationException("price_cache_authority_invalid");
        }

        if (point.AssetId != asset.Id
            || !string.Equals(point.ProviderSource, asset.Source, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("price_cache_asset_identity_invalid");
        }

        return new PriceCacheEntry(
            asset.Symbol, asset.Source, asset.Id,
            requestedDate, maxDays, point,
            catalog?.Revision ?? 0,
            catalog is null ? string.Empty : Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant());
    }

    private bool MatchesIdentity(AssetReadIdentity asset) =>
        Point is not null
        && string.Equals(Symbol, asset.Symbol, StringComparison.Ordinal)
        && string.Equals(AssetSource, asset.Source, StringComparison.Ordinal)
        && AssetId == asset.Id
        && Point.AssetId == asset.Id
        && string.Equals(Point.ProviderSource, asset.Source, StringComparison.Ordinal)
        && FinalObservationAuthority.IsCompleteFinal(Point);

    private bool MatchesCatalog(AssetCatalogVersion catalog) =>
        catalog.IsValid
        && CatalogRevision == catalog.Revision
        && string.Equals(
            CatalogHash,
            Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant(),
            StringComparison.Ordinal);
}

internal sealed record PriceRangeCacheEntry(
    string Symbol,
    string AssetSource,
    Guid AssetId,
    DateOnly From,
    DateOnly To,
    string Interval,
    IReadOnlyList<PricePoint?>? Points,
    long CatalogRevision = 0,
    string CatalogHash = "")
{
    internal static PriceRangeCacheEntry Create(
        AssetReadIdentity asset,
        DateOnly from,
        DateOnly to,
        string interval,
        IReadOnlyList<PricePoint> points)
        => Create(asset, from, to, interval, points, null);

    internal static PriceRangeCacheEntry Create(
        AssetReadIdentity asset,
        DateOnly from,
        DateOnly to,
        string interval,
        IReadOnlyList<PricePoint> points,
        AssetCatalogVersion? catalog)
    {
        if (points.Count == 0)
            return new(
                asset.Symbol, asset.Source, asset.Id, from, to, interval, points,
                catalog?.Revision ?? 0,
                catalog is null ? string.Empty : Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant());

        var first = points[0];
        if (!FinalObservationAuthority.IsCompleteFinal(first)
            || string.IsNullOrWhiteSpace(first.ProviderSource)
            || first.AssetId == Guid.Empty)
        {
            throw new InvalidOperationException("price_cache_authority_invalid");
        }

        var entry = new PriceRangeCacheEntry(
            asset.Symbol, asset.Source, asset.Id,
            from, to, interval, points.Cast<PricePoint?>().ToArray(),
            catalog?.Revision ?? 0,
            catalog is null ? string.Empty : Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant());
        if (!entry.IsValid(asset, from, to, interval))
            throw new InvalidOperationException("price_range_cache_identity_invalid");

        return entry;
    }

    internal bool IsValid(AssetReadIdentity asset, DateOnly from, DateOnly to, string interval)
    {
        if (!string.Equals(Symbol, asset.Symbol, StringComparison.Ordinal)
            || !string.Equals(AssetSource, asset.Source, StringComparison.Ordinal)
            || AssetId != asset.Id
            || From != from || To != to
            || !string.Equals(Interval, interval, StringComparison.Ordinal))
        {
            return false;
        }

        if (Points is null)
            return false;

        if (Points.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(AssetSource) || AssetId == Guid.Empty)
            return false;

        DateOnly? previous = null;
        foreach (var point in Points)
        {
            if (point is null
                || !FinalObservationAuthority.IsCompleteFinal(point)
                || point.AssetId != AssetId
                || !string.Equals(point.ProviderSource, AssetSource, StringComparison.Ordinal)
                || point.PriceDate < from || point.PriceDate > to
                || (previous.HasValue && point.PriceDate <= previous.Value))
            {
                return false;
            }

            previous = point.PriceDate;
        }

        return true;
    }

    internal bool IsValid(
        AssetReadIdentity asset,
        DateOnly from,
        DateOnly to,
        string interval,
        AssetCatalogVersion catalog) =>
        IsValid(asset, from, to, interval)
        && catalog.IsValid
        && CatalogRevision == catalog.Revision
        && string.Equals(
            CatalogHash,
            Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant(),
            StringComparison.Ordinal);
}

internal sealed record LatestPriceDateCacheEntry(
    string Symbol,
    string AssetSource,
    Guid AssetId,
    DateOnly Date,
    long CatalogRevision = 0,
    string CatalogHash = "")
{
    internal bool IsValid(AssetReadIdentity asset) =>
        string.Equals(Symbol, asset.Symbol, StringComparison.Ordinal)
        && string.Equals(AssetSource, asset.Source, StringComparison.Ordinal)
        && AssetId == asset.Id;

    internal bool IsValid(AssetReadIdentity asset, AssetCatalogVersion catalog) =>
        IsValid(asset)
        && catalog.IsValid
        && CatalogRevision == catalog.Revision
        && string.Equals(
            CatalogHash,
            Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant(),
            StringComparison.Ordinal);
}

internal sealed record AssetInfoCacheEntry(
    string Signature,
    string Language,
    IReadOnlyList<AssetReadIdentity>? Identities,
    IReadOnlyList<AssetResponse>? Assets,
    long CatalogRevision = 0,
    string CatalogHash = "")
{
    internal bool IsValid(
        string signature,
        string language,
        IReadOnlyList<AssetReadIdentity> identities)
    {
        if (Identities is null
            || Assets is null
            || !string.Equals(Signature, signature, StringComparison.Ordinal)
            || !string.Equals(Language, language, StringComparison.Ordinal)
            || !int.TryParse(signature, out var expectedCount)
            || expectedCount < 0
            || Assets.Count != expectedCount
            || Identities.Count != expectedCount
            || identities.Count != expectedCount)
        {
            return false;
        }

        if (!Identities.OrderBy(identity => identity.Symbol).SequenceEqual(
                identities.OrderBy(identity => identity.Symbol)))
        {
            return false;
        }

        if (identities.Any(identity => identity.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(identity.Symbol)
                || string.IsNullOrWhiteSpace(identity.Source))
            || identities.Select(identity => identity.Symbol)
                .Distinct(StringComparer.Ordinal).Count() != expectedCount)
        {
            return false;
        }

        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Symbol)
                || !symbols.Add(asset.Symbol)
                || (asset.FirstPriceDate.HasValue != asset.LastPriceDate.HasValue)
                || (asset.FirstPriceDate.HasValue
                    && asset.FirstPriceDate.Value > asset.LastPriceDate!.Value))
            {
                return false;
            }
        }

        return symbols.SetEquals(identities.Select(identity => identity.Symbol));
    }

    internal bool IsValid(
        string signature,
        string language,
        IReadOnlyList<AssetReadIdentity> identities,
        AssetCatalogVersion catalog) =>
        IsValid(signature, language, identities)
        && catalog.IsValid
        && CatalogRevision == catalog.Revision
        && string.Equals(
            CatalogHash,
            Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant(),
            StringComparison.Ordinal);
}

internal sealed record AssetListCacheEntry(
    long CatalogRevision,
    string CatalogHash,
    IReadOnlyList<Asset>? Assets)
{
    internal bool IsValid(AssetCatalogVersion catalog) =>
        Assets is not null
        && catalog.IsValid
        && CatalogRevision == catalog.Revision
        && string.Equals(
            CatalogHash,
            Convert.ToHexString(catalog.CatalogSha256).ToLowerInvariant(),
            StringComparison.Ordinal)
        && Assets.All(static asset => asset is not null && asset.IsActive)
        && Assets.Select(static asset => asset.Symbol)
            .Distinct(StringComparer.Ordinal).Count() == Assets.Count;
}
