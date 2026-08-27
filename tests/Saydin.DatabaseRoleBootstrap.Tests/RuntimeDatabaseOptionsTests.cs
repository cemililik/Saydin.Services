using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.Tests;

public sealed class RuntimeDatabaseOptionsTests
{
    private const string SystemHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ExactManagedLoginAndAllowlistedTopology_AreAccepted()
    {
        var environment = ValidEnvironment(LoginPurpose.Api);

        var options = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Api, RuntimeDatabasePooling.Service,
            key => environment.GetValueOrDefault(key));

        Assert.Equal("postgres", options.Host);
        Assert.Equal(5432, options.Port);
        Assert.Equal(LoginPurpose.Api, options.Purpose);
        Assert.Equal(RuntimeDatabasePooling.Service, options.Pooling);
    }

    [Theory]
    [InlineData("DATABASE_URL")]
    [InlineData("ConnectionStrings__Postgres")]
    [InlineData("PGPASSWORD")]
    [InlineData("PGPASSFILE")]
    [InlineData("POSTGRES_PASSWORD")]
    [InlineData("POSTGRES_EXPORTER_PASSWORD")]
    [InlineData("DATA_SOURCE_NAME")]
    [InlineData("SAYDIN_API_DATABASE_URL")]
    [InlineData("SAYDIN_INGESTION_DATABASE_URL")]
    [InlineData("SAYDIN_CALENDAR_DATABASE_URL")]
    [InlineData("SAYDIN_CALENDAR_IMPORTER_DATABASE_URL")]
    [InlineData("SAYDIN_EXPORTER_DATABASE_URL")]
    [InlineData("SAYDIN_AUDIT_DATABASE_URL")]
    [InlineData("SAYDIN_DATA_QUALITY_AUDIT_DATABASE_URL")]
    [InlineData("SAYDIN_MIGRATOR_DATABASE_URL")]
    public void RawSecretEnvironment_IsRejectedWithoutEcho(string name)
    {
        var environment = ValidEnvironment(LoginPurpose.Api);
        environment[name] = "Password=SENTINEL-runtime-secret";

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            RuntimeDatabaseOptions.FromEnvironment(
                LoginPurpose.Api, RuntimeDatabasePooling.Service,
                key => environment.GetValueOrDefault(key)));

        Assert.Equal("runtime_database_raw_secret_environment_rejected", exception.Code);
        Assert.DoesNotContain("SENTINEL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongManagedLogin_IsRejected()
    {
        var environment = ValidEnvironment(LoginPurpose.Api);
        environment["PGUSER"] = "postgres";

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            RuntimeDatabaseOptions.FromEnvironment(
                LoginPurpose.Api, RuntimeDatabasePooling.Service,
                key => environment.GetValueOrDefault(key)));

        Assert.Equal("runtime_database_login_contract_mismatch", exception.Code);
    }

    [Theory]
    [InlineData("postgres,standby")]
    [InlineData(" postgres")]
    [InlineData("postgres/path")]
    [InlineData("postgres host")]
    public void MultiHostOrAmbiguousHost_IsRejected(string host)
    {
        var environment = ValidEnvironment(LoginPurpose.Api);
        environment["PGHOST"] = host;

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            RuntimeDatabaseOptions.FromEnvironment(
                LoginPurpose.Api, RuntimeDatabasePooling.Service,
                key => environment.GetValueOrDefault(key)));

        Assert.Equal("runtime_database_environment_invalid", exception.Code);
    }

    [Fact]
    public void ExplicitVersionTwoLogin_IsAccepted()
    {
        var environment = ValidEnvironment(LoginPurpose.Api);
        var contract = RoleContract.Create(
            environment["SAYDIN_DEPLOYMENT_ID"]!, environment["PGDATABASE"]!,
            environment["SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256"]!,
            environment["SAYDIN_DATABASE_ROLE_PREFIX"]!);
        environment["SAYDIN_DATABASE_LOGIN_VERSION"] = "2";
        environment["PGUSER"] = contract.Login(LoginPurpose.Api, 2).Name;

        var options = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Api, RuntimeDatabasePooling.Service,
            key => environment.GetValueOrDefault(key));

        Assert.Equal(contract.Login(LoginPurpose.Api, 2).Name, options.Login.Name);
    }

    [Fact]
    public void Omitted_pgsslmode_defaults_to_require()
    {
        var environment = ValidEnvironment(LoginPurpose.Api);
        environment.Remove("PGSSLMODE");

        var options = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Api, RuntimeDatabasePooling.Service,
            key => environment.GetValueOrDefault(key));

        Assert.Equal(Npgsql.SslMode.Require, options.SslMode);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("prefer")]
    [InlineData("bogus")]
    public void AmbiguousOrInvalidSslModes_AreRejected(string sslMode)
    {
        var environment = ValidEnvironment(LoginPurpose.Api);
        environment["PGSSLMODE"] = sslMode;

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            RuntimeDatabaseOptions.FromEnvironment(
                LoginPurpose.Api, RuntimeDatabasePooling.Service,
                key => environment.GetValueOrDefault(key)));

        Assert.Equal("runtime_database_ssl_mode_invalid", exception.Code);
    }

    private static Dictionary<string, string?> ValidEnvironment(LoginPurpose purpose)
    {
        var prefix = RoleContract.DerivePrefix("dev-a", "saydin", SystemHash);
        var contract = RoleContract.Create("dev-a", "saydin", SystemHash, prefix);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PGHOST"] = "postgres",
            ["PGPORT"] = "5432",
            ["PGDATABASE"] = "saydin",
            ["PGUSER"] = contract.Login(purpose, 1).Name,
            ["PGSSLMODE"] = "disable",
            ["SAYDIN_DEPLOYMENT_ID"] = "dev-a",
            ["SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256"] = SystemHash,
            ["SAYDIN_DATABASE_ROLE_PREFIX"] = prefix,
            ["SAYDIN_DATABASE_LOGIN_VERSION"] = "1",
            [RuntimeDatabaseOptions.PasswordFileEnvironment(purpose)] = "/run/secrets/password",
        };
    }
}
