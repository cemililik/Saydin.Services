using FluentAssertions;
using Saydin.Migrations;

namespace Saydin.DatabaseMigrator.Tests;

[Trait("Category", "Unit")]
public sealed class MigrationManifestTests
{
    [Fact]
    public void Load_MigrationDirectory_ReturnsTwentySevenRawByteChecksumsInOrdinalOrder()
    {
        var manifest = MigrationManifest.Load(TestPaths.MigrationsDirectory);

        manifest.Migrations.Should().HaveCount(27);
        MigratorMigrationTrustRoot.Versions.Should().HaveCount(27);
        MigratorMigrationTrustRoot.Checksums.Keys.Should()
            .BeEquivalentTo(MigratorMigrationTrustRoot.Versions);
        manifest.Migrations.Select(item => item.Version)
            .Should().Equal(MigratorMigrationTrustRoot.Versions);
        manifest.Migrations.Select(item => item.Version).Should().BeInAscendingOrder(StringComparer.Ordinal);
        manifest.Migrations.Should().OnlyContain(item => item.Checksum.Length == 64);
        manifest.Migrations.Single(item => item.Kind == MigrationKind.OptionalExporterRole)
            .Version.Should().Be("012b_create_exporter_role");
        var rehashSql = manifest.Migrations.Single(item =>
            item.Version == "024_installation_credential_rehash").ReadSql();
        rehashSql.Should().Contain("IF p_key_version=p_active_key_version THEN")
            .And.Contain("credential.secret_hash=p_secret_hash")
            .And.Contain("credential.secret_hash=p_active_secret_hash")
            .And.Contain("FOR UPDATE OF credential;")
            .And.NotContain("FOR UPDATE OF credential,principal;");
    }

    [Fact]
    public void ValidateTrustedPrefix_OrderedSqlTail_IsAdmittedForImpactValidation()
    {
        using var directory = CopyHistoricalDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "026_impact_tail.sql"),
            "CREATE TABLE public.must_never_execute(id integer);\n");
        var manifest = MigrationManifest.Load(directory.Path);

        var action = () => MigrationRunner.ValidateTrustedPrefix(manifest);

        action.Should().NotThrow();
    }

    [Fact]
    public void ValidateTrustedPrefix_CanonicalPrefixWithoutTail_IsAccepted()
    {
        var manifest = MigrationManifest.Load(TestPaths.MigrationsDirectory);

        var action = () => MigrationRunner.ValidateTrustedPrefix(manifest);

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData("015_ingestion_windows.sql")]
    [InlineData("016_ingestion_write_fence.sql")]
    [InlineData("017_authoritative_market_calendars.sql")]
    [InlineData("018_scenario_integrity.sql")]
    [InlineData("019_privilege_separation.sql")]
    [InlineData("020_price_authority_expand.sql")]
    [InlineData("021_api_trust_expand.sql")]
    [InlineData("022_principal_retention.sql")]
    [InlineData("023_installation_lifecycle_admission.sql")]
    [InlineData("024_installation_credential_rehash.sql")]
    [InlineData("025_ingestion_calendar_rebind.sql")]
    public void ValidateTrustedPrefix_OmittedCanonicalMigration_Rejects(string omittedFile)
    {
        using var directory = CopyHistoricalDirectory();
        File.Delete(Path.Combine(directory.Path, omittedFile));
        var manifest = MigrationManifest.Load(directory.Path);

        var action = () => MigrationRunner.ValidateTrustedPrefix(manifest);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("historical_manifest_mismatch");
    }

    [Fact]
    public void Load_OneRawByteChanges_ChangesChecksum()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "001_test.sql");
        File.WriteAllBytes(path, "SELECT 1;\n"u8.ToArray());
        var first = MigrationManifest.Load(directory.Path);
        File.WriteAllBytes(path, "SELECT 1;\r\n"u8.ToArray());

        var second = MigrationManifest.Load(directory.Path);

        second.Migrations[0].Checksum.Should().NotBe(first.Migrations[0].Checksum);
        second.Checksum.Should().NotBe(first.Checksum);
    }

    [Fact]
    public void Load_UnknownShellMigration_Rejects()
    {
        using var directory = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(directory.Path, "999_unknown.sh"), "#!/bin/sh\n");

        var act = () => MigrationManifest.Load(directory.Path);

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("shell_migration_unsupported");
    }

    [Fact]
    public void ValidateTrustedPrefix_OneChangedHistoricalByte_RejectsPinnedTrustRoot()
    {
        using var directory = CopyHistoricalDirectory();
        var path = Path.Combine(directory.Path, "001_initial.sql");
        File.AppendAllText(path, "\n-- accidental historical edit\n");
        var manifest = MigrationManifest.Load(directory.Path);

        var act = () => MigrationRunner.ValidateTrustedPrefix(manifest);

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("historical_checksum_mismatch");
    }

    [Theory]
    [InlineData("015_ingestion_windows.sql")]
    [InlineData("016_ingestion_write_fence.sql")]
    [InlineData("017_authoritative_market_calendars.sql")]
    [InlineData("018_scenario_integrity.sql")]
    [InlineData("020_price_authority_expand.sql")]
    [InlineData("021_api_trust_expand.sql")]
    [InlineData("022_principal_retention.sql")]
    public void ValidateTrustedPrefix_ChangedAdditiveByte_RejectsPinnedMigration(string fileName)
    {
        using var directory = CopyHistoricalDirectory();
        var path = Path.Combine(directory.Path, fileName);
        File.AppendAllText(path, "\n-- accidental deployed migration edit\n");
        var manifest = MigrationManifest.Load(directory.Path);

        var act = () => MigrationRunner.ValidateTrustedPrefix(manifest);

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("pinned_checksum_mismatch");
    }

    private static TemporaryDirectory CopyHistoricalDirectory()
    {
        var directory = TemporaryDirectory.Create();
        foreach (var source in Directory.EnumerateFiles(TestPaths.MigrationsDirectory))
        {
            if (Path.GetExtension(source) is ".sql" or ".sh")
                File.Copy(source, Path.Combine(directory.Path, Path.GetFileName(source)));
        }
        return directory;
    }
}
