using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;
using System.Text.RegularExpressions;

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

        model.FindEntityType(typeof(ActivityLog))!.GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(User))
            .DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
    }

    [Fact]
    public void EfModel_CheckConstraintNames_CoverCheckedInMigrationOwnedTables()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var migrationDirectory = new DirectoryInfo(
            Path.Combine(FindRepositoryRoot().FullName, "infrastructure", "postgres", "migrations"));
        var migrationNames = new HashSet<string>(StringComparer.Ordinal);
        var constraintTransition = new Regex(
            @"(?i)\bCONSTRAINT\s+(?<add>chk_[a-z0-9_]+)\s+CHECK\b|" +
            @"\bDROP\s+CONSTRAINT(?:\s+IF\s+EXISTS)?\s+(?<drop>chk_[a-z0-9_]+)",
            RegexOptions.CultureInvariant);
        foreach (var file in migrationDirectory.GetFiles("*.sql")
                     .OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            foreach (Match transition in constraintTransition.Matches(File.ReadAllText(file.FullName)))
            {
                if (transition.Groups["add"].Success)
                    migrationNames.Add(transition.Groups["add"].Value);
                else
                    migrationNames.Remove(transition.Groups["drop"].Value);
            }
        }
        var modelNames = model.GetEntityTypes()
            .SelectMany(entity => entity.GetCheckConstraints())
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);

        string[] ownedPrefixes =
        [
            "chk_activity_", "chk_asset_catalog_", "chk_asset_market_",
            "chk_inflation_rates_", "chk_installation_credentials_",
            "chk_market_calendar", "chk_price_points_", "chk_users_",
        ];
        var expected = migrationNames
            .Where(name => ownedPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

        modelNames.Should().Contain(expected);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Saydin.Services.sln")))
        {
            directory = directory.Parent;
        }
        return directory
               ?? throw new InvalidOperationException("Repository root could not be located.");
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
