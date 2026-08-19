using System.Text.RegularExpressions;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DataQualityAudit.IntegrationTests;

internal sealed record IntegrationEnvironment(
    string AdminConnectionString,
    string RunId,
    string ExpectedHost,
    string DatabaseName,
    string DeploymentId,
    string SystemIdentifierSha256,
    string RolePrefix,
    string AuditLogin,
    string AuditPasswordFile,
    DateTimeOffset BackupV1ValidUntilUtc)
{
    private static readonly Regex RunIdPattern = new(
        "^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IntegrationEnvironment Require()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SAYDIN_AUDIT_TEST_REQUIRED"),
                "true", StringComparison.Ordinal))
            throw new InvalidOperationException("SAYDIN_AUDIT_TEST_REQUIRED=true is required; suite never skips.");
        var connection = SecureSecretFile.ReadConnectionString(
            RequireValue("SAYDIN_AUDIT_TEST_ADMIN_CONNECTION_FILE"));
        var runId = RequireValue("SAYDIN_AUDIT_TEST_RUN_ID");
        var expectedHost = RequireValue("SAYDIN_AUDIT_TEST_EXPECTED_HOST");
        var deploymentId = RequireValue("SAYDIN_AUDIT_TEST_DEPLOYMENT_ID");
        var systemHash = RequireValue("SAYDIN_AUDIT_TEST_SYSTEM_IDENTIFIER_SHA256");
        var rolePrefix = RequireValue("SAYDIN_AUDIT_TEST_ROLE_PREFIX");
        var auditLogin = RequireValue("SAYDIN_AUDIT_TEST_LOGIN");
        var auditPasswordFile = RequireValue("SAYDIN_AUDIT_TEST_PASSWORD_FILE");
        var backupValidUntilText = RequireValue("SAYDIN_BACKUP_V1_VALID_UNTIL");
        if (!RoleContract.TryParseBackupValidUntil(
                backupValidUntilText, out var backupV1ValidUntilUtc))
            throw new InvalidOperationException("Audit backup v1 validity must be canonical UTC seconds.");
        if (!RunIdPattern.IsMatch(runId))
            throw new InvalidOperationException("Audit test run id must be 32 lowercase hexadecimal characters.");

        var expectedDatabase = $"saydin_data_audit_test_{runId}";
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var actualHost = builder.Host ?? string.Empty;
        var actualDatabase = builder.Database ?? string.Empty;
        if (!string.Equals(actualDatabase, expectedDatabase, StringComparison.Ordinal) ||
            !string.Equals(actualHost, expectedHost, StringComparison.Ordinal) ||
            actualHost.Contains("prod", StringComparison.OrdinalIgnoreCase) ||
            actualHost.Contains("staging", StringComparison.OrdinalIgnoreCase) ||
            actualDatabase.Contains("prod", StringComparison.OrdinalIgnoreCase) ||
            actualDatabase.Contains("staging", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe audit integration database target.");
        return new IntegrationEnvironment(
            connection, runId, expectedHost, expectedDatabase, deploymentId, systemHash,
            rolePrefix, auditLogin, auditPasswordFile, backupV1ValidUntilUtc);
    }

    private static string RequireValue(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required; suite never skips.");
}
