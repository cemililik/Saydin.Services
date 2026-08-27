using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;
using System.Text.RegularExpressions;

namespace Saydin.Api.Tests.Data;

public sealed class ApiTrustSchemaModelTests
{
    [Fact]
    public void EfModel_InstallationCredential_MatchesMigration021TrustBoundaries()
    {
        using var context = CreateContext();
        var entity = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(InstallationCredential))!;
        var migration = Migration021();

        entity.GetTableName().Should().Be("installation_credentials");
        entity.GetCheckConstraints().Select(check => check.Name).Should().BeEquivalentTo(
            "chk_installation_credentials_generation",
            "chk_installation_credentials_hash_key_version",
            "chk_installation_credentials_secret_hash",
            "chk_installation_credentials_state",
            "chk_installation_credentials_lifecycle",
            "chk_installation_credentials_expiry",
            "chk_installation_credentials_rotation");

        entity.GetIndexes().Single(index =>
                index.GetDatabaseName() == "uq_installation_credentials_verifier")
            .Properties.Select(property => property.Name)
            .Should().Equal(
                nameof(InstallationCredential.HashKeyVersion),
                nameof(InstallationCredential.SecretHash));
        entity.GetIndexes().Single(index =>
                index.GetDatabaseName() == "uq_installation_credentials_active_principal")
            .GetFilter().Should().Be("state = 'active'");
        entity.FindAnnotation(
                "Saydin:DatabaseIndex:uq_installation_credentials_pending_principal")!
            .Value.Should().Be("UNIQUE (principal_id) WHERE state = 'pending'");
        entity.GetIndexes().Single(index =>
                index.GetDatabaseName() == "uq_installation_credentials_rotation_id")
            .GetFilter().Should().Be("rotation_id IS NOT NULL");

        entity.GetForeignKeys().Single(foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(User))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        entity.GetForeignKeys().Single(foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(InstallationCredential))
            .DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

        var modelObjectNames = entity.GetCheckConstraints().Select(item => item.Name)
            .Concat(entity.GetIndexes().Select(item => item.GetDatabaseName()))
            .Concat(entity.GetForeignKeys().Select(item => item.GetConstraintName()))
            .Append(entity.FindAnnotation(
                    "Saydin:DatabaseIndex:uq_installation_credentials_pending_principal") is null
                ? null
                : "uq_installation_credentials_pending_principal")
            .Where(name => name is not null)
            .Cast<string>()
            .Where(name => Regex.IsMatch(name, "^(chk|fk|uq)_installation_credentials_"));
        var migrationObjectNames = Regex.Matches(
                migration, @"\b(?:chk|fk|uq)_installation_credentials_[a-z_]+\b")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal);
        modelObjectNames.Should().BeEquivalentTo(migrationObjectNames,
            "the checked-in migration, not a duplicated test constant, is the schema authority");
    }

    [Fact]
    public void EfModel_PrincipalAndCatalogState_MatchMigration021ExpandContract()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var user = model.FindEntityType(typeof(User))!;
        var catalog = model.FindEntityType(typeof(AssetCatalogState))!;

        user.GetCheckConstraints().Select(check => check.Name).Should().Contain(
            "chk_users_tier",
            "chk_users_principal_status",
            "chk_users_principal_contract_version",
            "chk_users_principal_lifecycle",
            "chk_users_principal_expiry");
        user.FindProperty(nameof(User.PrincipalStatus))!.GetDefaultValue()
            .Should().Be("legacy_quarantined");
        user.FindProperty(nameof(User.PrincipalContractVersion))!.GetDefaultValue()
            .Should().Be(1);
        user.FindProperty(nameof(User.PrincipalQuarantinedAt))!.GetDefaultValueSql()
            .Should().Be("statement_timestamp()");

        catalog.GetTableName().Should().Be("asset_catalog_state");
        catalog.FindPrimaryKey()!.Properties.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(AssetCatalogState.Singleton));
        catalog.GetCheckConstraints().Select(check => check.Name).Should().BeEquivalentTo(
            "chk_asset_catalog_state_singleton",
            "chk_asset_catalog_state_revision",
            "chk_asset_catalog_state_sha256");

        var migration = Migration021();
        foreach (var name in user.GetCheckConstraints().Select(check => check.Name)
                     .Where(name => name is not null && name.StartsWith(
                         "chk_users_principal_", StringComparison.Ordinal))
                     .Concat(catalog.GetCheckConstraints().Select(check => check.Name)))
            migration.Should().Contain(name!);
    }

    private static SaydinDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SaydinDbContext>()
            .UseNpgsql("Host=localhost;Database=saydin_api_trust_model;Username=test;Password=test")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new SaydinDbContext(options);
    }

    private static string Migration021() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "infrastructure", "postgres", "migrations",
        "021_api_trust_expand.sql"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Saydin.Services.sln")))
            current = current.Parent;
        return current?.FullName
            ?? throw new InvalidOperationException("Repository root could not be located.");
    }
}
