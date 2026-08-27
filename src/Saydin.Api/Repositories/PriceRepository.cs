using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public sealed class PriceRepository(SaydinDbContext context) : IPriceRepository
{
    private static readonly Expression<Func<PricePoint, PricePoint>> ApiProjection = point => new PricePoint
    {
        AssetId = point.AssetId,
        PriceDate = point.PriceDate,
        Close = point.Close,
        Open = point.Open,
        High = point.High,
        Low = point.Low,
        Volume = point.Volume,
        ProviderSource = point.ProviderSource,
        SourceObservationId = point.SourceObservationId,
        AsOfAt = point.AsOfAt,
        PriceKind = point.PriceKind,
        IsFinal = point.IsFinal,
        ObservationSha256 = point.ObservationSha256,
        AuthorityContractVersion = point.AuthorityContractVersion,
        HasSourceRaw = point.SourceRaw != null,
    };

    public async Task<IReadOnlyList<Asset>> GetAllActiveAssetsAsync(CancellationToken ct)
        => await context.Assets
            .Where(a => a.IsActive)
            .OrderBy(a => a.Category)
            .ThenBy(a => a.Symbol)
            .ToListAsync(ct);

    public Task<AssetReadIdentity?> GetActiveAssetIdentityAsync(
        string symbol,
        CancellationToken ct) => context.Assets
        .AsNoTracking()
        .Where(asset => asset.IsActive && asset.Symbol == symbol)
        .Select(asset => new AssetReadIdentity(asset.Id, asset.Symbol, asset.Source))
        .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<AssetReadIdentity>> GetAllActiveAssetIdentitiesAsync(
        CancellationToken ct) => await context.Assets
        .AsNoTracking()
        .Where(asset => asset.IsActive)
        .OrderBy(asset => asset.Symbol)
        .Select(asset => new AssetReadIdentity(asset.Id, asset.Symbol, asset.Source))
        .ToListAsync(ct);

    public Task<int> GetActiveAssetCountAsync(CancellationToken ct)
        => context.Assets.CountAsync(a => a.IsActive, ct);

    public async Task<AssetCatalogVersion> GetAssetCatalogVersionAsync(CancellationToken ct)
    {
        var version = await context.Database
            .SqlQueryRaw<AssetCatalogVersion>(
                """
                SELECT revision, catalog_sha256
                FROM public.get_asset_catalog_state()
                """)
            .SingleAsync(ct);

        return version.IsValid
            ? version
            : throw new InvalidOperationException("Asset catalog version is invalid.");
    }

    public async Task<PricePoint?> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct)
        => await context.PricePoints
            .AsNoTracking()
            .WhereCompleteFinalAuthority()
            .Where(pp => pp.Asset.Symbol == symbol && pp.PriceDate == date)
            .Select(ApiProjection)
            .FirstOrDefaultAsync(ct);

    public async Task<PricePoint?> GetNearestPriceAsync(
        string symbol, DateOnly date, int maxDays, CancellationToken ct)
    {
        // F2.3-2 ([C-C-4]): Tek sorgu — aday penceredeki tüm noktalar arasında
        // "backward öncelikli" sıralama. Önceki sürüm happy path'te bile her
        // çağrıda 2 roundtrip yapıyordu (backward, sonra forward fallback).
        // Yeni sürümde:
        //   1) priority = 0 → PriceDate ≤ date (backward),
        //                = 1 → PriceDate > date (forward),
        //   2) priority içinde target'a en yakın gün önce.
        // Backward grup içinde "en büyük PriceDate" (date'e en yakın),
        // forward grup içinde "en küçük PriceDate" (date'in hemen üstündeki gün).
        var minDate = date.AddDays(-maxDays);
        var maxDate = date.AddDays(maxDays);

        return await context.PricePoints
            .AsNoTracking()
            .WhereCompleteFinalAuthority()
            .Where(pp => pp.Asset.Symbol == symbol
                      && pp.PriceDate >= minDate
                      && pp.PriceDate <= maxDate)
            .OrderBy(pp => pp.PriceDate > date ? 1 : 0)
            .ThenByDescending(pp => pp.PriceDate <= date ? pp.PriceDate : minDate)
            .ThenBy(pp => pp.PriceDate > date ? pp.PriceDate : maxDate)
            .Select(ApiProjection)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<PricePoint?>> GetNearestPricesAsync(
        string symbol,
        IReadOnlyList<DateOnly> dates,
        int maxDays,
        CancellationToken ct)
    {
        const int MaxRequests = 601;
        if (dates.Count is < 1 or > MaxRequests)
            throw new ArgumentOutOfRangeException(nameof(dates));
        if (maxDays is < 0 or > 31)
            throw new ArgumentOutOfRangeException(nameof(maxDays));

        // WITH ORDINALITY is the contract here: duplicate requested dates retain
        // separate logical positions while one LATERAL lookup applies the exact
        // production backward-first rule to each position. The complete authority
        // predicate intentionally mirrors FinalObservationAuthority.
        const string sql =
            """
            WITH requested AS
            (
                SELECT requested_date, ordinality
                  FROM unnest(@requested_dates::date[]) WITH ORDINALITY
                       AS input(requested_date, ordinality)
            )
            SELECT requested.ordinality,
                   nearest.asset_id,
                   nearest.price_date,
                   nearest.close,
                   nearest.open,
                   nearest.high,
                   nearest.low,
                   nearest.volume,
                   nearest.provider_source,
                   nearest.source_observation_id,
                   nearest.as_of_at,
                   nearest.price_kind,
                   nearest.is_final,
                   nearest.observation_sha256,
                   nearest.authority_contract_version,
                   nearest.has_source_raw
              FROM requested
              LEFT JOIN LATERAL
              (
                  SELECT point.asset_id,
                         point.price_date,
                         point.close,
                         point.open,
                         point.high,
                         point.low,
                         point.volume,
                         point.provider_source,
                         point.source_observation_id,
                         point.as_of_at,
                         point.price_kind,
                         point.is_final,
                         point.observation_sha256,
                         point.authority_contract_version,
                         point.source_raw IS NOT NULL AS has_source_raw
                    FROM price_points AS point
                    JOIN assets AS asset ON asset.id = point.asset_id
                   WHERE asset.symbol = @symbol
                     AND point.price_date BETWEEN requested.requested_date - @max_days
                                              AND requested.requested_date + @max_days
                     AND point.is_final IS TRUE
                     AND point.provider_source IS NOT NULL
                     AND point.source_observation_id IS NOT NULL
                     AND point.as_of_at IS NOT NULL
                     AND point.price_kind IS NOT NULL
                     AND point.observation_sha256 IS NOT NULL
                     AND octet_length(point.observation_sha256) = 32
                     AND point.authority_contract_version > 0
                     AND point.source_raw IS NOT NULL
                     AND point.provider_source = asset.source
                     AND ((point.provider_source = 'tcmb'
                           AND point.price_kind = 'official_reference')
                       OR (point.provider_source = 'coingecko'
                           AND point.price_kind = 'daily_utc_reference')
                       OR (point.provider_source = 'openexchangerates'
                           AND point.price_kind = 'daily_reference')
                       OR (point.provider_source = 'twelvedata'
                           AND point.price_kind = 'daily_close'))
                   ORDER BY CASE WHEN point.price_date <= requested.requested_date THEN 0 ELSE 1 END,
                            CASE WHEN point.price_date <= requested.requested_date
                                 THEN requested.requested_date - point.price_date
                                 ELSE point.price_date - requested.requested_date END,
                            point.price_date
                   LIMIT 1
              ) AS nearest ON TRUE
             ORDER BY requested.ordinality
            """;

        var rows = await context.Database.SqlQueryRaw<BulkNearestPriceRow>(
                sql,
                new NpgsqlParameter(
                // NpgsqlDbType.Array is combined with the element type by design; this is
                // Npgsql's documented spelling for an array parameter, not a flags misuse.
                "requested_dates", NpgsqlDbType.Array | NpgsqlDbType.Date) // NOSONAR
                {
                    Value = dates.ToArray(),
                },
                new NpgsqlParameter("symbol", NpgsqlDbType.Text)
                {
                    Value = symbol,
                },
                new NpgsqlParameter("max_days", NpgsqlDbType.Integer)
                {
                    Value = maxDays,
                })
            .ToListAsync(ct);

        if (rows.Count != dates.Count)
            throw new InvalidOperationException("nearest_price_batch_cardinality_invalid");

        return rows.Select(row => row.AssetId is null
            ? null
            : new PricePoint
            {
                AssetId = row.AssetId.Value,
                PriceDate = row.PriceDate!.Value,
                Close = row.Close!.Value,
                Open = row.Open,
                High = row.High,
                Low = row.Low,
                Volume = row.Volume,
                ProviderSource = row.ProviderSource,
                SourceObservationId = row.SourceObservationId,
                AsOfAt = row.AsOfAt,
                PriceKind = row.PriceKind,
                IsFinal = row.IsFinal,
                ObservationSha256 = row.ObservationSha256,
                AuthorityContractVersion = row.AuthorityContractVersion,
                HasSourceRaw = row.HasSourceRaw is true,
            })
            .ToArray();
    }

    private sealed class BulkNearestPriceRow
    {
        public long Ordinality { get; init; }
        public Guid? AssetId { get; init; }
        public DateOnly? PriceDate { get; init; }
        public decimal? Close { get; init; }
        public decimal? Open { get; init; }
        public decimal? High { get; init; }
        public decimal? Low { get; init; }
        public decimal? Volume { get; init; }
        public string? ProviderSource { get; init; }
        public string? SourceObservationId { get; init; }
        public DateTimeOffset? AsOfAt { get; init; }
        public string? PriceKind { get; init; }
        public bool? IsFinal { get; init; }
        public byte[]? ObservationSha256 { get; init; }
        public int? AuthorityContractVersion { get; init; }
        public bool? HasSourceRaw { get; init; }
    }

    public async Task<DateOnly?> GetLatestPriceDateAsync(string symbol, CancellationToken ct)
        => await context.PricePoints
            .AsNoTracking()
            .WhereCompleteFinalAuthority()
            .Where(pp => pp.Asset.Symbol == symbol)
            .Select(pp => (DateOnly?)pp.PriceDate)
            .MaxAsync(ct);

    public async Task<IReadOnlyList<(Asset Asset, DateOnly? FirstDate, DateOnly? LastDate)>>
        GetAllActiveAssetsWithDateRangesAsync(CancellationToken ct)
    {
        var assets = await context.Assets
            .Where(a => a.IsActive)
            .OrderBy(a => a.Category)
            .ThenBy(a => a.Symbol)
            .ToListAsync(ct);

        if (assets.Count == 0)
            return Array.Empty<(Asset, DateOnly?, DateOnly?)>();

        var assetIds = assets.Select(a => a.Id).ToHashSet();

        var ranges = await context.PricePoints
            .AsNoTracking()
            .WhereCompleteFinalAuthority()
            .Where(pp => assetIds.Contains(pp.AssetId))
            .GroupBy(pp => pp.AssetId)
            .Select(g => new
            {
                AssetId   = g.Key,
                FirstDate = g.Min(pp => (DateOnly?)pp.PriceDate),
                LastDate  = g.Max(pp => (DateOnly?)pp.PriceDate),
            })
            .ToDictionaryAsync(r => r.AssetId, ct);

        return assets
            .Select(a =>
            {
                ranges.TryGetValue(a.Id, out var r);
                return (a, r?.FirstDate, r?.LastDate);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct)
        => await context.PricePoints
            .AsNoTracking()
            .WhereCompleteFinalAuthority()
            .Where(pp => pp.Asset.Symbol == symbol && pp.PriceDate >= from && pp.PriceDate <= to)
            .OrderBy(pp => pp.PriceDate)
            .Select(ApiProjection)
            .ToListAsync(ct);
}
