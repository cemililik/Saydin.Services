using System.Data;
using Dapper;
using Npgsql;
using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public sealed class PriceRepository(string connectionString) : IPriceRepository
{
    /// <summary>Program.cs'den çağrılır — Dapper global TypeHandler'larını kaydeder.</summary>
    public static void RegisterTypeHandlers()
    {
        SqlMapper.AddTypeHandler(new AssetCategoryTypeHandler());
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
    }

    public async Task<IReadOnlyList<Asset>> GetAllActiveAssetsAsync(CancellationToken ct)
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
            WHERE is_active = TRUE
            ORDER BY category, symbol
            """);

        return assets.AsList().AsReadOnly();
    }

    public async Task<PricePoint?> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        return await conn.QueryFirstOrDefaultAsync<PricePoint>(
            """
            SELECT
                pp.asset_id   AS AssetId,
                pp.price_date AS PriceDate,
                pp.close      AS Close,
                pp.open       AS Open,
                pp.high       AS High,
                pp.low        AS Low,
                pp.volume     AS Volume
            FROM price_points pp
            JOIN assets a ON a.id = pp.asset_id
            WHERE a.symbol = @symbol
              AND pp.price_date = @date
            """,
            new { symbol, date });
    }

    public async Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var points = await conn.QueryAsync<PricePoint>(
            """
            SELECT
                pp.asset_id   AS AssetId,
                pp.price_date AS PriceDate,
                pp.close      AS Close,
                pp.open       AS Open,
                pp.high       AS High,
                pp.low        AS Low,
                pp.volume     AS Volume
            FROM price_points pp
            JOIN assets a ON a.id = pp.asset_id
            WHERE a.symbol = @symbol
              AND pp.price_date BETWEEN @from AND @to
            ORDER BY pp.price_date
            """,
            new { symbol, from, to });

        return points.AsList().AsReadOnly();
    }
}

/// <summary>Dapper için DateOnly ↔ PostgreSQL date dönüştürücü.</summary>
internal sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = System.Data.DbType.Date;
        parameter.Value  = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d  => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _           => DateOnly.FromDateTime(Convert.ToDateTime(value))
    };
}

/// <summary>
/// Dapper için PostgreSQL asset_category enum → C# AssetCategory dönüştürücü.
/// DB değerleri snake_case ('precious_metal'), C# değerleri PascalCase ('PreciousMetal').
/// </summary>
internal sealed class AssetCategoryTypeHandler : SqlMapper.TypeHandler<AssetCategory>
{
    public override void SetValue(IDbDataParameter parameter, AssetCategory value)
        => parameter.Value = value switch
        {
            AssetCategory.Currency      => "currency",
            AssetCategory.PreciousMetal => "precious_metal",
            AssetCategory.Stock         => "stock",
            AssetCategory.Crypto        => "crypto",
            _                           => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public override AssetCategory Parse(object value)
        => value.ToString() switch
        {
            "currency"       => AssetCategory.Currency,
            "precious_metal" => AssetCategory.PreciousMetal,
            "stock"          => AssetCategory.Stock,
            "crypto"         => AssetCategory.Crypto,
            _                => throw new InvalidOperationException($"Bilinmeyen asset_category: {value}")
        };
}
