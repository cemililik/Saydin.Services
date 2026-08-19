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

    public Task UpsertPricePointsAsync(IReadOnlyList<PricePoint> pricePoints, CancellationToken ct) =>
        throw new InvalidOperationException("window_bound_authority_repository_required");

    /// <summary>
    /// F2.4-9 ([G-D-04]): Belirli bir asset için aralıkta DB'de **var olan** (mevcut)
    /// price_date gün setini döner. Caller "gap" kümesini, beklenen tarih aralığından
    /// bu seti çıkararak hesaplar (<see cref="Workers.BaseAssetWorker.ComputeMissingRanges"/>).
    /// Backfill ettiği yer "latestDate sonrası tek blok" varsayımını terk eder —
    /// geçmişte bir worker ortası kalan boşluklar da kapanır.
    /// </summary>
    public async Task<IReadOnlySet<DateOnly>> GetExistingDatesAsync(
        Guid assetId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dates = await context.PricePoints
            .Where(pp => pp.AssetId == assetId
                      && pp.PriceDate >= from
                      && pp.PriceDate <= to)
            .Select(pp => pp.PriceDate)
            .ToListAsync(ct);
        return dates.ToHashSet();
    }

    public async Task<DateOnly?> GetLatestPriceDateAsync(Guid assetId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.PricePoints
            .Where(pp => pp.AssetId == assetId)
            .MaxAsync(pp => (DateOnly?)pp.PriceDate, ct);
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
