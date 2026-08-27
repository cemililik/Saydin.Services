using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class SharedEfParityIntegrationTests(DatabaseFixture db)
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetDelete_WithLoadedOrUnloadedPriceDependency_ReachesDatabaseRestrict23503(
        bool loadDependent)
    {
        Skip.IfNot(db.Available, db.SkipReason);
        var assetId = Guid.CreateVersion7();
        var priceDate = new DateOnly(2096, 8, 19);
        await using (var setup = db.CreateAdminContext())
        {
            await using var transaction = await setup.Database.BeginTransactionAsync();
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO assets(id,symbol,display_name,category,is_active,source)
                VALUES ({assetId},{($"EF{assetId:N}"[..20]).ToUpperInvariant()},'EF parity','crypto',TRUE,'coingecko');
                SET LOCAL session_replication_role='replica';
                INSERT INTO price_points(asset_id,price_date,close)
                VALUES ({assetId},{priceDate},1);
                """);
            await transaction.CommitAsync();
        }

        try
        {
            await using var context = db.CreateAdminContext();
            context.ChangeTracker.CascadeDeleteTiming = CascadeTiming.Never;
            Asset asset;
            if (loadDependent)
            {
                asset = await context.Assets
                    .Include(entry => entry.PricePoints)
                    .SingleAsync(entry => entry.Id == assetId);
                asset.PricePoints.Should().ContainSingle();

                // DeleteBehavior.Restrict intentionally has a client-side guard for
                // tracked required dependents. Detach only after proving the navigation
                // was loaded so both branches exercise the database FK authority and
                // must surface the exact PostgreSQL 23503 contract.
                foreach (var dependent in asset.PricePoints)
                    context.Entry(dependent).State = EntityState.Detached;
            }
            else
            {
                asset = await context.Assets.SingleAsync(entry => entry.Id == assetId);
            }

            context.Assets.Remove(asset);
            var failure = await FluentActions.Awaiting(() => context.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateException>();
            failure.Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        }
        finally
        {
            await using var cleanup = db.CreateAdminContext();
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM price_points WHERE asset_id={assetId} AND price_date={priceDate};
                DELETE FROM assets WHERE id={assetId};
                """);
        }
    }
}
