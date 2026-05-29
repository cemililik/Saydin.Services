using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Repositories;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

/// <summary>
/// F2.7-5: composite PK (period_date, source) gerçek PostgreSQL'de doğrulanır — aynı ay
/// için seed-approximation + tuik satırları bir arada tutulur ve okuma yolu tuik'i tercih eder.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class InflationRepositoryIntegrationTests(DatabaseFixture db)
{
    [SkippableFact]
    public async Task GetIndexValuesAsync_PrefersTuikOverSeed_ForSameMonth()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        Skip.IfNot(db.CompositeInflationPk,
            "Migration 012 (composite PK period_date,source) DB'ye uygulanmamış — fresh init veya 012 runbook gerekli.");

        // Üretim/seed verisiyle çakışmayan uzak-gelecek ay.
        var period = new DateOnly(2099, 12, 1);

        await using (var seed = db.CreateContext())
        {
            // Composite PK sayesinde aynı ay için iki kaynak yan yana eklenebilir.
            seed.InflationRates.Add(new InflationRate
            {
                PeriodDate = period, IndexValue = 100m, Source = InflationSources.SeedApproximation,
            });
            seed.InflationRates.Add(new InflationRate
            {
                PeriodDate = period, IndexValue = 200m, Source = InflationSources.Tuik,
            });
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var ctx = db.CreateContext();
            var repo = new InflationRepository(ctx);

            var (buyIdx, buyDate, _, _) = await repo.GetIndexValuesAsync(
                new DateOnly(2099, 12, 15), new DateOnly(2099, 12, 15), CancellationToken.None);

            buyIdx.Should().Be(200m, "tuik kaynağı seed-approximation'a tercih edilmeli (F2.7-5)");
            buyDate.Should().Be(period);
        }
        finally
        {
            // Paylaşılan tabloyu kirletme — eklenen test satırlarını sil.
            await using var cleanup = db.CreateContext();
            await cleanup.InflationRates
                .Where(r => r.PeriodDate == period)
                .ExecuteDeleteAsync();
        }
    }
}
