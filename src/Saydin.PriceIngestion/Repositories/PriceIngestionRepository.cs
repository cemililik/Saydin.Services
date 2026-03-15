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

        // ON CONFLICT ile idempotent UPSERT — EF Core raw SQL (parameterized, injection-safe)
        foreach (var point in pricePoints)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO price_points (asset_id, price_date, close, open, high, low, volume)
                VALUES ({point.AssetId}, {point.PriceDate}, {point.Close}, {point.Open}, {point.High}, {point.Low}, {point.Volume})
                ON CONFLICT (asset_id, price_date) DO UPDATE
                    SET close = EXCLUDED.close,
                        open  = EXCLUDED.open,
                        high  = EXCLUDED.high,
                        low   = EXCLUDED.low
                """,
                ct);
        }
    }

    public async Task<DateOnly?> GetLatestPriceDateAsync(Guid assetId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.PricePoints
            .Where(pp => pp.AssetId == assetId)
            .MaxAsync(pp => (DateOnly?)pp.PriceDate, ct);
    }
}
