using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

/// <summary>
/// EF Core tabanlı enflasyon ingestion repository.
/// BackgroundService (singleton) içinden çağrıldığı için IDbContextFactory kullanır.
/// </summary>
public sealed class InflationIngestionRepository(IDbContextFactory<SaydinDbContext> contextFactory)
    : IInflationIngestionRepository
{
    public async Task<DateOnly?> GetLatestInflationDateAsync(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.InflationRates
            .MaxAsync(r => (DateOnly?)r.PeriodDate, ct);
    }

    public async Task UpsertInflationRatesAsync(IReadOnlyList<InflationRate> rates, CancellationToken ct)
    {
        if (rates.Count == 0) return;

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        foreach (var rate in rates)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO inflation_rates (period_date, index_value, source, created_at, updated_at)
                VALUES ({rate.PeriodDate}, {rate.IndexValue}, {rate.Source}, {rate.CreatedAt}, {rate.UpdatedAt})
                ON CONFLICT (period_date) DO UPDATE
                    SET index_value = EXCLUDED.index_value,
                        updated_at  = EXCLUDED.updated_at
                """,
                ct);
        }
    }
}
