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
    public async Task<DateOnly?> GetLatestInflationDateAsync(string source, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        // INGR-012: yalnız verilen source'un (EVDS için 'tuik') max period_date'i. Tüm kaynakların
        // max'ı seed verisini (2010→2025) kapsadığından gerçek tuik backfill'ini atlıyordu.
        return await context.InflationRates
            .Where(r => r.Source == source)
            .MaxAsync(r => (DateOnly?)r.PeriodDate, ct);
    }

    public Task UpsertInflationRatesAsync(IReadOnlyList<InflationRate> rates, CancellationToken ct) =>
        throw new InvalidOperationException("window_bound_authority_repository_required");
}
