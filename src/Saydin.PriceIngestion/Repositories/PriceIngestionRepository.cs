using Dapper;
using Npgsql;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

public sealed class PriceIngestionRepository(string connectionString) : IPriceIngestionRepository
{
    static PriceIngestionRepository()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
    }

    public async Task<IReadOnlyList<Asset>> GetActiveAssetsBySourceAsync(string source, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var assets = await conn.QueryAsync<Asset>(
            """
            SELECT
                id           AS Id,
                symbol       AS Symbol,
                display_name AS DisplayName,
                CASE category::text
                    WHEN 'currency'       THEN 'Currency'
                    WHEN 'precious_metal' THEN 'PreciousMetal'
                    WHEN 'stock'          THEN 'Stock'
                    WHEN 'crypto'         THEN 'Crypto'
                END AS Category,
                source       AS Source,
                source_id    AS SourceId,
                is_active    AS IsActive
            FROM assets
            WHERE source = @source
              AND is_active = TRUE
            """,
            new { source });

        return assets.AsList().AsReadOnly();
    }

    public async Task UpsertPricePointsAsync(IReadOnlyList<PricePoint> pricePoints, CancellationToken ct)
    {
        if (pricePoints.Count == 0) return;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var point in pricePoints)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO price_points (asset_id, price_date, close, open, high, low, volume)
                VALUES (@AssetId, @PriceDate, @Close, @Open, @High, @Low, @Volume)
                ON CONFLICT (asset_id, price_date) DO UPDATE
                    SET close      = EXCLUDED.close,
                        open       = EXCLUDED.open,
                        high       = EXCLUDED.high,
                        low        = EXCLUDED.low,
                        updated_at = NOW()
                """,
                point,
                tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<DateOnly?> GetLatestPriceDateAsync(Guid assetId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var result = await conn.QueryFirstOrDefaultAsync<DateTime?>(
            "SELECT MAX(price_date) FROM price_points WHERE asset_id = @assetId",
            new { assetId });

        return result.HasValue ? DateOnly.FromDateTime(result.Value) : null;
    }
}

/// <summary>Dapper için DateOnly ↔ PostgreSQL date dönüştürücü.</summary>
file sealed class DateOnlyTypeHandler : Dapper.SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(System.Data.IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = System.Data.DbType.Date;
        parameter.Value  = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value)
        => DateOnly.FromDateTime(Convert.ToDateTime(value));
}
