using System.Text.RegularExpressions;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DataRepair.IntegrationTests;

internal sealed record RepairIntegrationEnvironment(
    string AdminConnectionString,
    string RunId,
    string Host,
    int Port,
    string Database,
    string DeploymentId,
    string SystemIdentifierSha256,
    string RolePrefix,
    string IngestionLogin,
    string IngestionPasswordFile,
    string AuditLogin,
    string AuditPasswordFile)
{
    private static readonly Regex RunIdPattern = new(
        "^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static RepairIntegrationEnvironment Require()
    {
        if (Environment.GetEnvironmentVariable("SAYDIN_REPAIR_TEST_REQUIRED") != "true")
            throw new InvalidOperationException(
                "SAYDIN_REPAIR_TEST_REQUIRED=true is required; this real-PG suite never skips.");
        var runId = Required("SAYDIN_REPAIR_TEST_RUN_ID");
        if (!RunIdPattern.IsMatch(runId))
            throw new InvalidOperationException("Repair test run id must be exact lowercase UUID hex.");
        var expectedHost = Required("SAYDIN_REPAIR_TEST_EXPECTED_HOST");
        var database = $"saydin_data_repair_test_{runId}";
        var admin = SecureSecretFile.ReadConnectionString(
            Required("SAYDIN_REPAIR_TEST_ADMIN_CONNECTION_FILE"));
        var builder = new NpgsqlConnectionStringBuilder(admin);
        if (builder.Host != expectedHost || builder.Database != database ||
            expectedHost.Contains("prod", StringComparison.OrdinalIgnoreCase) ||
            expectedHost.Contains("staging", StringComparison.OrdinalIgnoreCase) ||
            database.Contains("prod", StringComparison.OrdinalIgnoreCase) ||
            database.Contains("staging", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe repair integration database target.");

        var deployment = Required("SAYDIN_REPAIR_TEST_DEPLOYMENT_ID");
        var systemHash = Required("SAYDIN_REPAIR_TEST_SYSTEM_IDENTIFIER_SHA256");
        var prefix = Required("SAYDIN_REPAIR_TEST_ROLE_PREFIX");
        var contract = RoleContract.Create(deployment, database, systemHash, prefix);
        var ingestion = Required("SAYDIN_REPAIR_TEST_INGESTION_LOGIN");
        var audit = Required("SAYDIN_REPAIR_TEST_AUDIT_LOGIN");
        if (ingestion != contract.Login(LoginPurpose.Ingestion, 1).Name ||
            audit != contract.Login(LoginPurpose.Audit, 1).Name)
            throw new InvalidOperationException("Repair managed login contract mismatch.");
        return new RepairIntegrationEnvironment(
            admin, runId, expectedHost, builder.Port, database, deployment, systemHash,
            prefix, ingestion, Required("SAYDIN_REPAIR_TEST_INGESTION_PASSWORD_FILE"),
            audit, Required("SAYDIN_REPAIR_TEST_AUDIT_PASSWORD_FILE"));
    }

    public string? RuntimeValue(string name) => name switch
    {
        "SAYDIN_ENVIRONMENT" => "development",
        "PGHOST" => Host,
        "PGPORT" => Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "PGDATABASE" => Database,
        "PGUSER" => IngestionLogin,
        "PGSSLMODE" => "disable",
        "SAYDIN_DEPLOYMENT_ID" => DeploymentId,
        "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" => SystemIdentifierSha256,
        "SAYDIN_DATABASE_ROLE_PREFIX" => RolePrefix,
        "SAYDIN_DATABASE_LOGIN_VERSION" => "1",
        "SAYDIN_INGESTION_DATABASE_PASSWORD_FILE" => IngestionPasswordFile,
        _ => null,
    };

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required; suite never skips.");
}
