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

        // Aynı (asset_id, price_date) için PostgreSQL'in ON CONFLICT cümlesi tek statement
        // içinde "cannot affect row a second time" hatası verir. Aynı pencerede TCMB
        // weekend chunk overlap'i veya retry sonrası duplikasyon ihtimaline karşı
        // önce dedupe ediyoruz; aynı keyden son gelen kayıt korunur.
        var deduped = pricePoints
            .GroupBy(p => new { p.AssetId, p.PriceDate })
            .Select(g => g.Last())
            .ToArray();

        // Batch UNNEST UPSERT — tek SQL ile N kayıt yazılır (önceden her satır için
        // ayrı INSERT round-trip vardı; 20 yıl backfill ~100k call → dakikalar).
        // InflationIngestionRepository ile aynı pattern.
        var assetIds   = deduped.Select(p => p.AssetId).ToArray();
        var priceDates = deduped.Select(p => p.PriceDate).ToArray();
        var closes     = deduped.Select(p => p.Close).ToArray();
        var opens      = deduped.Select(p => p.Open).ToArray();
        var highs      = deduped.Select(p => p.High).ToArray();
        var lows       = deduped.Select(p => p.Low).ToArray();
        var volumes    = deduped.Select(p => p.Volume).ToArray();

        // F2.4-8 ([C-D-41]): UNNEST batch tek statement olsa da, gelecekteki
        // multi-statement extension'lara (örn. ingestion_jobs aynı transaction'da
        // yazımı) hazırlık olarak transaction'a sar. Tek statement içinde
        // ON CONFLICT atomik olduğu için kısa transaction maliyeti ihmal edilebilir.
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        try
        {
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
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

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
}
