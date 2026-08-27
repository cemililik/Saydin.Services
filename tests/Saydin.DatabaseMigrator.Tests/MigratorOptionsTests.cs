using FluentAssertions;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseMigrator.Tests;

[Trait("Category", "Unit")]
public sealed class MigratorOptionsTests
{
    private const string SystemHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("DATABASE_URL")]
    [InlineData("PGPASSWORD")]
    [InlineData("POSTGRES_EXPORTER_PASSWORD")]
    public void Parse_rejects_secret_environment(string variable)
    {
        var environment = Environment();
        environment[variable] = "secret-sentinel";

        var act = () => MigratorOptions.Parse([], environment);

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("secret_environment_rejected");
    }

    [Fact]
    public void Parse_builds_single_host_nonsecret_contract()
    {
        var options = MigratorOptions.Parse([], Environment());

        options.SafeTarget.Should().Be(
            $"host=postgres;port=5433;database=saydin;user={options.ExpectedLogin}");
        options.ToString().ToLowerInvariant().Should().NotContain("password");
        options.Contract.TargetLockSha256.Should().HaveLength(64);
        options.SslMode.Should().Be(Npgsql.SslMode.Require,
            "missing TLS configuration must fail closed to encrypted transport");
    }

    [Theory]
    [InlineData("db-a,db-b")]
    [InlineData("db a")]
    [InlineData("")]
    public void Parse_rejects_non_single_host(string host)
    {
        var environment = Environment();
        environment["PGHOST"] = host;

        var act = () => MigratorOptions.Parse([], environment);

        act.Should().Throw<MigratorRejectedException>();
    }

    [Fact]
    public void Parse_rejects_verify_only_cutover_combination()
    {
        var act = () => MigratorOptions.Parse(
            ["--verify-only", "--legacy-privilege-cutover", "--admin-connection-file", "/run/secrets/admin"],
            Environment());

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("legacy_cutover_verify_only_rejected");
    }

    [Theory]
    [InlineData("SAYDIN_MIGRATION_IMPACT_DIR", "/run/release/impact")]
    [InlineData("SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_FILE", "/run/release/impact.pem")]
    [InlineData("SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_SHA256",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Parse_rejects_partial_impact_configuration(string variable, string value)
    {
        var environment = Environment();
        environment[variable] = value;

        var act = () => MigratorOptions.Parse([], environment);

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_configuration_invalid");
    }

    [Fact]
    public void Parse_accepts_complete_public_impact_verifier_contract()
    {
        var environment = Environment();
        environment["SAYDIN_MIGRATION_IMPACT_DIR"] = "/run/release/impact";
        environment["SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_FILE"] = "/run/release/impact.pem";
        environment["SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_SHA256"] = SystemHash;

        var options = MigratorOptions.Parse([], environment);

        options.ImpactConfiguration.Should().NotBeNull();
        options.ImpactConfiguration!.Directory.Should().Be("/run/release/impact");
        options.ToString().Should().NotContain(SystemHash);
    }

    private static Dictionary<string, string?> Environment()
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);
        var contract = RoleContract.Create("prod-a", "saydin", SystemHash, prefix);
        return new Dictionary<string, string?>
        {
            ["PGHOST"] = "postgres",
            ["PGPORT"] = "5433",
            ["PGDATABASE"] = "saydin",
            ["PGUSER"] = contract.Login(LoginPurpose.Migrator, 1).Name,
            ["SAYDIN_MIGRATOR_PASSWORD_FILE"] = "/run/secrets/migrator",
            ["SAYDIN_DEPLOYMENT_ID"] = contract.DeploymentId,
            ["SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256"] = SystemHash,
            ["SAYDIN_DATABASE_ROLE_PREFIX"] = prefix,
            ["SAYDIN_TIMESCALEDB_VERSION"] = "2.16.1",
            ["SAYDIN_UUID_OSSP_VERSION"] = "1.1",
            ["SAYDIN_BACKUP_V1_VALID_UNTIL"] = "2026-10-19T00:00:00Z",
        };
    }
}
