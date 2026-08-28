using System.Security.Cryptography;
using System.Text;
using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class EmbeddedMigrationTests
{
    [Fact]
    public void EmbeddedManifest_PinsEveryCurrentMigrationRawByteChecksum()
    {
        var manifest = EmbeddedMigrations.Load();
        var root = FindRepositoryRoot();
        manifest.Migrations.Should().HaveCount(27);
        EmbeddedMigrations.PinnedChecksums.Should().HaveCount(27);
        EmbeddedMigrations.PinnedChecksums.Keys.Should().BeEquivalentTo(
            manifest.Migrations.Select(item => item.Version));
        manifest.Migrations.Single(item => item.Version == EmbeddedMigrations.ScenarioIntegrityVersion)
            .Checksum.Should().Be(EmbeddedMigrations.ScenarioIntegrityChecksum);
        manifest.Migrations.Single(item => item.Version == EmbeddedMigrations.ApiTrustVersion)
            .Checksum.Should().Be(EmbeddedMigrations.ApiTrustChecksum);
        manifest.Migrations.Single(item => item.Version == EmbeddedMigrations.PrincipalRetentionVersion)
            .Checksum.Should().Be(EmbeddedMigrations.PrincipalRetentionChecksum);
        manifest.Migrations.Single(item =>
                item.Version == EmbeddedMigrations.InstallationLifecycleAdmissionVersion)
            .Checksum.Should().Be(EmbeddedMigrations.InstallationLifecycleAdmissionChecksum);
        manifest.Migrations.Single(item =>
                item.Version == EmbeddedMigrations.InstallationCredentialRehashVersion)
            .Checksum.Should().Be(EmbeddedMigrations.InstallationCredentialRehashChecksum);
        foreach (var embedded in manifest.Migrations)
        {
            var file = Path.Combine(root, "infrastructure", "postgres", "migrations", embedded.FileName);
            embedded.Checksum.Should().Be(
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
        }

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var migration in manifest.Migrations)
        {
            incremental.AppendData(Encoding.UTF8.GetBytes(migration.Version));
            incremental.AppendData([0]);
            incremental.AppendData(Encoding.ASCII.GetBytes(migration.Checksum));
            incremental.AppendData([0]);
        }
        manifest.Checksum.Should().Be(Convert.ToHexStringLower(incremental.GetHashAndReset()));
    }

    [Theory]
    [InlineData("015_ingestion_windows")]
    [InlineData("016_ingestion_write_fence")]
    [InlineData("017_authoritative_market_calendars")]
    [InlineData("018_scenario_integrity")]
    [InlineData("019_privilege_separation")]
    [InlineData("020_price_authority_expand")]
    [InlineData("021_api_trust_expand")]
    [InlineData("022_principal_retention")]
    [InlineData("023_installation_lifecycle_admission")]
    [InlineData("024_installation_credential_rehash")]
    [InlineData("025_ingestion_calendar_rebind")]
    public void PinnedManifest_ChangedAdditiveMigrationByte_Rejects(string version)
    {
        var root = FindRepositoryRoot();
        var fileName = EmbeddedMigrations.Load().Migrations.Single(item => item.Version == version).FileName;
        var original = File.ReadAllBytes(Path.Combine(
            root, "infrastructure", "postgres", "migrations", fileName));
        var copiedMutation = original.Concat("\n-- copied mutation\n"u8.ToArray()).ToArray();
        var changed = Convert.ToHexStringLower(SHA256.HashData(copiedMutation));

        var action = () => EmbeddedMigrations.ValidatePinnedChecksum(version, changed);

        action.Should().Throw<AuditRejectedException>()
            .Which.Code.Should().Be("embedded_migration_checksum_mismatch");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Saydin.Services.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
