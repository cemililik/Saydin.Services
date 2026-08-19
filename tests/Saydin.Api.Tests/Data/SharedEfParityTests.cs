using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Data;

public sealed class SharedEfParityTests
{
    [Fact]
    public void EfModel_AssetDependents_AreRestrictAndDatabaseTimestampsAreReadOnly()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var price = model.FindEntityType(typeof(PricePoint))!;
        price.GetForeignKeys().Single(foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(Asset))
            .DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

        var job = model.FindEntityType(typeof(IngestionJob))!;
        job.GetForeignKeys().Single(foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(Asset))
            .DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

        AssertDatabaseGeneratedReadOnly(
            price.FindProperty(nameof(PricePoint.IngestedAt))!);
        AssertDatabaseGeneratedReadOnly(
            model.FindEntityType(typeof(Asset))!
                .FindProperty(nameof(Asset.CreatedAt))!);
    }

    private static void AssertDatabaseGeneratedReadOnly(IReadOnlyProperty property)
    {
        property.ValueGenerated.Should().Be(ValueGenerated.OnAdd);
        property.GetDefaultValueSql().Should().Be("NOW()");
        property.GetBeforeSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
        property.GetAfterSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
    }

    private static SaydinDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SaydinDbContext>()
            .UseNpgsql("Host=localhost;Database=saydin_shared_parity_model;Username=test;Password=test")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new SaydinDbContext(options);
    }
}
