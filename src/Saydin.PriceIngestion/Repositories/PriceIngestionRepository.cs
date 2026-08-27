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
            .OrderBy(a => a.Symbol)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<DateOnly>> GetMarketHolidaysAsync(
        Guid assetId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dates = await context.Database
            .SqlQuery<DateOnly>($"""
                SELECT holiday_date AS "Value"
                  FROM market_holidays
                 WHERE asset_id = {assetId}
                   AND holiday_date BETWEEN {from} AND {to}
                """)
            .ToListAsync(ct);
        return dates.ToHashSet();
    }
}
