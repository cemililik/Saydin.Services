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

        // Tek SQL ile batch upsert — round-trip sayısını azaltır
        var periodDates = rates.Select(r => r.PeriodDate).ToArray();
        var indexValues = rates.Select(r => r.IndexValue).ToArray();
        var sources     = rates.Select(r => r.Source).ToArray();
        var createdAts  = rates.Select(r => r.CreatedAt).ToArray();
        var updatedAts  = rates.Select(r => r.UpdatedAt).ToArray();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inflation_rates (period_date, index_value, source, created_at, updated_at)
            SELECT * FROM UNNEST(
                {periodDates}::date[],
                {indexValues}::numeric[],
                {sources}::text[],
                {createdAts}::timestamptz[],
                {updatedAts}::timestamptz[]
            )
            ON CONFLICT (period_date) DO UPDATE
                SET index_value = EXCLUDED.index_value,
                    updated_at  = EXCLUDED.updated_at
            """,
            ct);
    }
}
