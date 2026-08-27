using System.Security.Cryptography;
using System.Text;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.Tests;

public sealed class RoleContractTests
{
    private const string SystemHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Prefix_is_deterministic_and_bound_to_all_target_dimensions()
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);

        Assert.StartsWith("saydin_pro_", prefix, StringComparison.Ordinal);
        Assert.Equal(prefix, RoleContract.DerivePrefix("prod-a", "saydin", SystemHash));
        Assert.NotEqual(prefix, RoleContract.DerivePrefix("prod-b", "saydin", SystemHash));
        Assert.NotEqual(prefix, RoleContract.DerivePrefix("prod-a", "saydin_b", SystemHash));
    }

    [Fact]
    public void Supplied_prefix_must_be_the_exact_derived_value()
    {
        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            RoleContract.Create("prod-a", "saydin", SystemHash,
                "saydin_pro_000000000000000000000000"));

        Assert.Equal("role_prefix_contract_mismatch", exception.Code);
        Assert.Equal(DatabaseSecurityFailureKind.TargetRejected, exception.Kind);
    }

    [Fact]
    public void Marker_parser_accepts_only_the_exact_marker_graph()
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);
        var contract = RoleContract.Create("prod-a", "saydin", SystemHash, prefix);
        var login = contract.Login(LoginPurpose.Api, 1);

        Assert.True(contract.TryParseManagedMarker(login.Marker, out var purpose, out var version));
        Assert.Equal("api", purpose);
        Assert.Equal(1, version);
        Assert.False(contract.TryParseManagedMarker(login.Marker + ";extra=true", out _, out _));
        Assert.False(contract.TryParseManagedMarker(
            login.Marker.Replace("kind=login", "kind=capability", StringComparison.Ordinal), out _, out _));
        var otherPurposeMarker = login.Marker.Replace(
            "purpose=api", "purpose=audit", StringComparison.Ordinal);
        Assert.True(contract.TryParseManagedMarker(otherPurposeMarker, out _, out _));
        Assert.False(contract.IsExactMarker(login, otherPurposeMarker));
    }

    [Fact]
    public void Contract_hash_binds_versions_roles_and_membership_topology()
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);
        var contract = RoleContract.Create("prod-a", "saydin", SystemHash, prefix);

        var baseline = contract.ContractSha256("2.23.1", "1.1");

        Assert.Equal("c994dd5bbb7e95a5c068f102644486d8c167275308158a6d042e9ab7a847badc", baseline);
        Assert.Matches("^[0-9a-f]{64}$", baseline);
        Assert.NotEqual(baseline, contract.ContractSha256("2.23.2", "1.1"));
        Assert.NotEqual(baseline, contract.ContractSha256("2.23.1", "1.2"));
        Assert.Equal(baseline, contract.ContractSha256("2.23.1", "1.1"));
        Assert.DoesNotContain("backup", contract.ContractMaterial("2.23.1", "1.1"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_contract_is_separate_finite_and_physical_replication_only()
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);
        var contract = RoleContract.Create("prod-a", "saydin", SystemHash, prefix);
        var validUntil = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        var v1 = contract.BackupLogin(1, validUntil);
        var v2 = contract.BackupLogin(2, validUntil.AddDays(1));

        Assert.True(v1.Replication);
        Assert.Equal(2, v1.ConnectionLimit);
        Assert.Equal(validUntil, v1.ValidUntilUtc);
        Assert.True(contract.TryResolveManagedMarker(v2.Marker, out var parsed));
        Assert.Equal(v2, parsed);
        Assert.Contains("database-connect=none", contract.BackupContractMaterial(
            "2.23.1", "1.1", validUntil), StringComparison.Ordinal);
        Assert.Contains("replication-protocol=physical", contract.BackupContractMaterial(
            "2.23.1", "1.1", validUntil), StringComparison.Ordinal);
        Assert.NotEqual(
            contract.BackupContractSha256("2.23.1", "1.1", validUntil),
            contract.BackupContractSha256("2.23.1", "1.1", validUntil.AddSeconds(1)));
    }

    [Fact]
    public void Contract_material_declares_exact_public_schema_usage_topology()
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);
        var contract = RoleContract.Create("prod-a", "saydin", SystemHash, prefix);
        var schemaUsage = contract.ContractMaterial("2.23.1", "1.1")
            .Split('\n', StringSplitOptions.None)
            .Single(line => line.StartsWith("schema-usage=", StringComparison.Ordinal));

        Assert.Equal("schema-usage=" + string.Join(',', new[]
        {
            contract.ApiCapability.Name,
            contract.IngestionCapability.Name,
            contract.CalendarImporterCapability.Name,
            contract.AuditCapability.Name,
            contract.TimescaleScheduler.Name,
        }), schemaUsage);
        Assert.DoesNotContain(contract.ExporterCapability.Name, schemaUsage, StringComparison.Ordinal);
    }

    [Fact]
    public void Target_lock_identity_is_only_physical_system_and_database()
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);
        var firstClaim = RoleContract.Create("prod-a", "saydin", SystemHash, prefix);
        var otherPrefix = RoleContract.DerivePrefix("prod-b", "saydin", SystemHash);
        var conflictingClaim = RoleContract.Create("prod-b", "saydin", SystemHash, otherPrefix);

        Assert.Equal(firstClaim.TargetLockSha256, conflictingClaim.TargetLockSha256);
        Assert.NotEqual(
            firstClaim.ContractSha256("2.23.1", "1.1"),
            conflictingClaim.ContractSha256("0.0.0", "1.1"));
        Assert.NotEqual(firstClaim.Prefix, conflictingClaim.Prefix);
        Assert.Matches("^[0-9a-f]{64}$", firstClaim.TargetLockSha256);
    }

    [Theory]
    [InlineData("xy")]
    [InlineData("Prod")]
    [InlineData("prod_a")]
    [InlineData("prod-a-too-long")]
    public void Deployment_id_is_bounded_and_canonical(string deployment)
    {
        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            RoleContract.DerivePrefix(deployment, "saydin", SystemHash));

        Assert.Equal("role_contract_invalid", exception.Code);
    }

    [Fact]
    public void Prefix_suffix_matches_documented_sha256_input()
    {
        var input = $"{SystemHash}\0saydin\0prod-a";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24];

        Assert.Equal($"saydin_pro_{expected}",
            RoleContract.DerivePrefix("prod-a", "saydin", SystemHash));
    }

    [Fact]
    public void Collision_resistant_prefix_keeps_supported_login_names_within_postgres_limit()
    {
        var prefix = RoleContract.DerivePrefix("production-a", "saydin", SystemHash);
        var contract = RoleContract.Create("production-a", "saydin", SystemHash, prefix);

        Assert.Equal(24, prefix[(prefix.LastIndexOf('_') + 1)..].Length);
        Assert.All(contract.AllRolesForVersion(2), role =>
            Assert.InRange(Encoding.UTF8.GetByteCount(role.Name), 1, 63));
        Assert.All(RoleContract.AllowedLoginVersions, version =>
            Assert.InRange(
                Encoding.UTF8.GetByteCount(contract.Login(LoginPurpose.CalendarImporter, version).Name),
                1, 63));
        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            contract.Login(LoginPurpose.CalendarImporter, 33));
        Assert.Equal("login_version_invalid", exception.Code);
    }
}
