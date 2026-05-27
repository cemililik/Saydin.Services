using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

/// <summary>
/// EF Core tabanlı ingestion repository.
/// BackgroundService (singleton) içinden çağrıldığı için IDbContextFactory kullanır;
/// her operasyon kendi kısa ömürlü DbContext'ini açar ve dispose eder.
/// </summary>
public sealed class PriceIngestionRepository(IDbContextFactory<SaydinDbContext> contextFactory)
    : IPriceIngestionRepository
{
    public async Task<IReadOnlyList<Asset>> GetActiveAssetsBySourceAsync(string source, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.Assets
            .Where(a => a.Source == source && a.IsActive)
            .ToListAsync(ct);
    }

    public async Task UpsertPricePointsAsync(IReadOnlyList<PricePoint> pricePoints, CancellationToken ct)
    {
        if (pricePoints.Count == 0) return;

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Batch UNNEST UPSERT — tek SQL ile N kayıt yazılır (önceden her satır için
        // ayrı INSERT round-trip vardı; 20 yıl backfill ~100k call → dakikalar).
        // InflationIngestionRepository ile aynı pattern.
        var assetIds   = pricePoints.Select(p => p.AssetId).ToArray();
        var priceDates = pricePoints.Select(p => p.PriceDate).ToArray();
        var closes     = pricePoints.Select(p => p.Close).ToArray();
        var opens      = pricePoints.Select(p => p.Open).ToArray();
        var highs      = pricePoints.Select(p => p.High).ToArray();
        var lows       = pricePoints.Select(p => p.Low).ToArray();
        var volumes    = pricePoints.Select(p => p.Volume).ToArray();

        // ingested_at sütununu NOW() ile dolduruyoruz — replay/backfill izlenebilir kalır.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO price_points (asset_id, price_date, close, open, high, low, volume, ingested_at)
            SELECT asset_id, price_date, close, open, high, low, volume, NOW()
            FROM UNNEST(
                {assetIds}::uuid[],
                {priceDates}::date[],
                {closes}::numeric[],
                {opens}::numeric[],
                {highs}::numeric[],
                {lows}::numeric[],
                {volumes}::numeric[]
            ) AS t(asset_id, price_date, close, open, high, low, volume)
            ON CONFLICT (asset_id, price_date) DO UPDATE
                SET close       = EXCLUDED.close,
                    open        = EXCLUDED.open,
                    high        = EXCLUDED.high,
                    low         = EXCLUDED.low,
                    volume      = EXCLUDED.volume,
                    ingested_at = EXCLUDED.ingested_at
            """,
            ct);
    }

    public async Task<DateOnly?> GetLatestPriceDateAsync(Guid assetId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.PricePoints
            .Where(pp => pp.AssetId == assetId)
            .MaxAsync(pp => (DateOnly?)pp.PriceDate, ct);
    }
}
