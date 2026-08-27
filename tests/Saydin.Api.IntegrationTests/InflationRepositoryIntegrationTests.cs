using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Repositories;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

/// <summary>
/// Final-only CPI visibility is verified against the real PostgreSQL migration-020
/// triggers and the historical all-null seed rows left by the expand phase.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class InflationRepositoryIntegrationTests(DatabaseFixture db)
{
    [SkippableFact]
    public async Task FinalAuthority_ExactAndLastKnownValue_IgnoreLegacySeedRows()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        Skip.IfNot(db.PriceAuthority, "Frozen migration 020 authority fingerprint is required.");
        await using var scenario = await AuthorityObservationScenario.CreateAsync(db);
        await using var context = db.CreateContext();
        var repository = new InflationRepository(context);

        var (buy, sell) = await repository.GetIndexValuesAsync(
            new DateOnly(2024, 3, 15), new DateOnly(2024, 4, 20), CancellationToken.None);

        buy.Should().NotBeNull();
        buy!.PeriodDate.Should().Be(AuthorityObservationScenario.FirstFinalCpiMonth,
            "March's all-null seed row must be invisible to LKV");
        buy.IndexValue.Should().Be(201m);
        sell.Should().NotBeNull();
        sell!.PeriodDate.Should().Be(AuthorityObservationScenario.LastFinalCpiMonth);
        sell.IndexValue.Should().Be(204m);

        var exact = await repository.GetExactIndexValuesAsync(
            [
                new DateOnly(2024, 1, 15),
                new DateOnly(2024, 2, 20),
                new DateOnly(2024, 2, 1),
                new DateOnly(2024, 3, 20),
                new DateOnly(2024, 4, 20),
            ],
            CancellationToken.None);

        exact.Keys.Should().BeEquivalentTo(new[]
        {
            AuthorityObservationScenario.FirstFinalCpiMonth,
            AuthorityObservationScenario.LastFinalCpiMonth,
        });
        exact[AuthorityObservationScenario.FirstFinalCpiMonth].Authority.ProviderSource
            .Should().Be("evds");
        exact.Values.Should().OnlyContain(index =>
            index.Authority.PriceKind == "cpi_index"
            && index.Authority.AuthorityContractVersion == 1);

        var terminal = await repository.GetLatestFinalIndexValueAsync(
            new DateOnly(2024, 4, 1), CancellationToken.None);
        terminal.Should().NotBeNull();
        terminal!.PeriodDate.Should().Be(AuthorityObservationScenario.LastFinalCpiMonth);
        terminal.IndexValue.Should().Be(204m);
    }

    [SkippableFact]
    public async Task Insert_WithChannelIdentitySource_IsRejectedByAuthorityBoundary()
    {
        // INGR-010 regresyon koruması (DB seviyesi): inflation_rates.source DATA kökenidir;
        // chk_inflation_rates_source yalnız 'tuik'/'seed-approximation' kabul eder. Kanal
        // kimliği "evds" buraya yazılırsa DB reddetmeli (EVDS worker artık Tuik yazıyor).
        Skip.IfNot(db.Available, db.SkipReason);

        await using var ctx = db.CreateAdminContext();
        ctx.InflationRates.Add(new InflationRate
        {
            PeriodDate = new DateOnly(2099, 11, 1), IndexValue = 1m, Source = "evds",
        });

        var act = () => ctx.SaveChangesAsync();

        // Migration 020's ALWAYS authority trigger is now the first fail-closed boundary;
        // it rejects the all-null authority tuple before the historical source CHECK.
        var failure = await act.Should().ThrowAsync<DbUpdateException>();
        var postgres = failure.Which.InnerException.Should().BeOfType<PostgresException>().Subject;
        postgres.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        postgres.ConstraintName.Should().Be("chk_inflation_rates_authority_tuple");
    }
}
