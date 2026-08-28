using System.Globalization;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseMigrator;

internal sealed record MigratorTimeouts(
    TimeSpan Connect,
    TimeSpan Lock,
    TimeSpan LockPoll,
    TimeSpan Command,
    TimeSpan Total);

internal sealed record MigratorOptions(
    string Host,
    int Port,
    string Database,
    string ExpectedLogin,
    string PasswordFile,
    string MigrationsDirectory,
    RoleContract Contract,
    string TimescaleVersion,
    string UuidOsspVersion,
    DateTimeOffset BackupV1ValidUntilUtc,
    int LoginVersion,
    SslMode SslMode,
    bool VerifyOnly,
    bool LegacyPrivilegeCutover,
    string? AdminConnectionFile,
    MigrationImpactConfiguration? ImpactConfiguration,
    MigratorTimeouts Timeouts)
{
    private static readonly string[] ForbiddenSecretEnvironment =
    [
        "DATABASE_URL", "PGPASSWORD", "POSTGRES_EXPORTER_PASSWORD",
    ];

    public string ContractSha256 => Contract.ContractSha256(TimescaleVersion, UuidOsspVersion);
    public string SafeTarget => $"host={Host};port={Port};database={Database};user={ExpectedLogin}";

    public NpgsqlConnectionStringBuilder BuildNormalConnection()
    {
        var password = SecureSecretFile.ReadPassword(PasswordFile);
        return Harden(new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = ExpectedLogin,
            Password = password,
            SslMode = SslMode,
        });
    }

    public NpgsqlConnectionStringBuilder BuildLegacyAdminConnection()
    {
        if (!LegacyPrivilegeCutover || AdminConnectionFile is null)
            throw new MigratorRejectedException("legacy_admin_connection_not_enabled");
        var secret = SecureSecretFile.ReadConnectionString(AdminConnectionFile);
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(secret);
        }
        catch (ArgumentException)
        {
            throw new MigratorRejectedException("legacy_admin_connection_invalid");
        }
        if (!string.Equals(builder.Host, Host, StringComparison.Ordinal) || builder.Port != Port ||
            !string.Equals(builder.Database, Database, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Username) ||
            string.IsNullOrWhiteSpace(builder.Password) ||
            builder.Host.Contains(',', StringComparison.Ordinal) || builder.Host.Any(char.IsWhiteSpace) ||
            builder.LoadBalanceHosts ||
            (!string.IsNullOrEmpty(builder.TargetSessionAttributes) &&
             !string.Equals(builder.TargetSessionAttributes, "any", StringComparison.OrdinalIgnoreCase)) ||
            builder.Multiplexing || !string.IsNullOrEmpty(builder.Options) ||
            !string.IsNullOrEmpty(builder.Passfile) || !string.IsNullOrEmpty(builder.SearchPath))
            throw new MigratorRejectedException("legacy_admin_target_mismatch");
        return Harden(new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = builder.Username,
            Password = builder.Password,
            SslMode = SslMode,
        });
    }

    public override string ToString() =>
        $"MigratorOptions({SafeTarget}; verify_only={VerifyOnly}; cutover={LegacyPrivilegeCutover}; migrations={MigrationsDirectory})";

    public static MigratorOptions Parse(string[] args, IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var key in ForbiddenSecretEnvironment)
            if (!string.IsNullOrEmpty(Get(environment, key)))
                throw new MigratorRejectedException("secret_environment_rejected", key);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var verifyOnly = false;
        var cutover = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--verify-only":
                    if (verifyOnly) throw new MigratorRejectedException("argument_duplicate");
                    verifyOnly = true;
                    break;
                case "--legacy-privilege-cutover":
                    if (cutover) throw new MigratorRejectedException("argument_duplicate");
                    cutover = true;
                    break;
                default:
                    if (!args[index].StartsWith("--", StringComparison.Ordinal) ||
                        index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) ||
                        !values.TryAdd(args[index], args[++index]))
                        throw new MigratorRejectedException("argument_unsupported");
                    break;
            }
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--host", "--port", "--target-database", "--expected-login", "--password-file",
            "--migrations-dir", "--deployment-id", "--system-identifier-sha256", "--role-prefix",
            "--timescaledb-version", "--uuid-ossp-version", "--login-version", "--ssl-mode",
            "--backup-v1-valid-until",
            "--migration-impact-dir", "--migration-impact-public-key-file",
            "--migration-impact-public-key-sha256",
            "--admin-connection-file", "--connect-timeout-seconds", "--lock-timeout-seconds",
            "--command-timeout-seconds", "--total-timeout-seconds",
        };
        if (values.Keys.Any(key => !allowed.Contains(key)))
            throw new MigratorRejectedException("argument_unsupported");

        var host = Value(values, environment, "--host", "PGHOST");
        if (host.Contains(',', StringComparison.Ordinal) || host.Any(char.IsWhiteSpace) ||
            host.Length > 253 || host.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ':')))
            throw new MigratorRejectedException("database_host_invalid");
        var database = Value(values, environment, "--target-database", "PGDATABASE");
        var expectedLogin = Value(values, environment, "--expected-login", "PGUSER");
        var passwordFile = Value(values, environment, "--password-file", "SAYDIN_MIGRATOR_PASSWORD_FILE");
        var deployment = Value(values, environment, "--deployment-id", "SAYDIN_DEPLOYMENT_ID");
        var systemHash = Value(values, environment, "--system-identifier-sha256",
            "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256");
        var prefix = Value(values, environment, "--role-prefix", "SAYDIN_DATABASE_ROLE_PREFIX");
        var timescaleVersion = Value(values, environment, "--timescaledb-version",
            "SAYDIN_TIMESCALEDB_VERSION");
        var uuidVersion = Value(values, environment, "--uuid-ossp-version", "SAYDIN_UUID_OSSP_VERSION");
        var backupV1ValidUntilText = Value(values, environment, "--backup-v1-valid-until",
            "SAYDIN_BACKUP_V1_VALID_UNTIL");
        if (!RoleContract.TryParseBackupValidUntil(backupV1ValidUntilText, out var backupV1ValidUntil))
            throw new MigratorRejectedException("backup_valid_until_invalid");
        var port = ParseInt(Value(values, environment, "--port", "PGPORT", "5432"), 1, 65535,
            "database_port_invalid");
        var loginVersion = ParseInt(Value(values, environment, "--login-version",
            "SAYDIN_MIGRATOR_LOGIN_VERSION", "1"), 1, 999, "login_version_invalid");
        var contract = RoleContract.Create(deployment, database, systemHash, prefix);
        if (!string.Equals(expectedLogin, contract.Login(LoginPurpose.Migrator, loginVersion).Name,
                StringComparison.Ordinal))
            throw new MigratorRejectedException("expected_migrator_login_mismatch");
        if (!Path.IsPathFullyQualified(passwordFile))
            throw new MigratorRejectedException("password_file_path_invalid");

        var migrationsDirectory = values.GetValueOrDefault("--migrations-dir") ??
            Get(environment, "SAYDIN_MIGRATIONS_DIR") ??
            Path.Combine(Environment.CurrentDirectory, "infrastructure", "postgres", "migrations");
        var adminFile = values.GetValueOrDefault("--admin-connection-file");
        if (cutover != (adminFile is not null) || adminFile is not null && !Path.IsPathFullyQualified(adminFile))
            throw new MigratorRejectedException("legacy_admin_file_contract_invalid");
        if (cutover && verifyOnly)
            throw new MigratorRejectedException("legacy_cutover_verify_only_rejected");

        var impactDirectory = values.GetValueOrDefault("--migration-impact-dir") ??
                              Get(environment, "SAYDIN_MIGRATION_IMPACT_DIR");
        var impactPublicKey = values.GetValueOrDefault("--migration-impact-public-key-file") ??
                              Get(environment, "SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_FILE");
        var impactPublicKeySha = values.GetValueOrDefault("--migration-impact-public-key-sha256") ??
                                 Get(environment, "SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_SHA256");
        var impactConfiguredCount = new[]
        {
            impactDirectory, impactPublicKey, impactPublicKeySha,
        }.Count(value => value is not null);
        if (impactConfiguredCount is not (0 or 3) || impactDirectory is not null &&
            (!Path.IsPathFullyQualified(impactDirectory) ||
             !Path.IsPathFullyQualified(impactPublicKey!) ||
             impactPublicKeySha!.Length != 64 || impactPublicKeySha.Any(character =>
                 character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new MigratorRejectedException("migration_impact_configuration_invalid");
        var impactConfiguration = impactDirectory is null
            ? null
            : new MigrationImpactConfiguration(
                Path.GetFullPath(impactDirectory), impactPublicKey!, impactPublicKeySha!);

        var sslModeText = values.GetValueOrDefault("--ssl-mode") ?? Get(environment, "PGSSLMODE") ?? "require";
        if (!Enum.TryParse<SslMode>(sslModeText.Replace("-", string.Empty, StringComparison.Ordinal),
                true, out var sslMode)
            || !Enum.IsDefined(sslMode))
            throw new MigratorRejectedException("ssl_mode_invalid");

        var timeouts = new MigratorTimeouts(
            Seconds(values, environment, "--connect-timeout-seconds", "SAYDIN_MIGRATOR_CONNECT_TIMEOUT_SECONDS", 10, 1, 60),
            Seconds(values, environment, "--lock-timeout-seconds", "SAYDIN_MIGRATOR_LOCK_TIMEOUT_SECONDS", 120, 1, 300),
            TimeSpan.FromMilliseconds(100),
            Seconds(values, environment, "--command-timeout-seconds", "SAYDIN_MIGRATOR_COMMAND_TIMEOUT_SECONDS", 1_800, 1, 3_600),
            Seconds(values, environment, "--total-timeout-seconds", "SAYDIN_MIGRATOR_TOTAL_TIMEOUT_SECONDS", 2_100, 5, 7_200));
        if (timeouts.Total <= timeouts.Connect || timeouts.Total <= timeouts.Lock)
            throw new MigratorRejectedException("timeout_contract_invalid");

        return new MigratorOptions(host, port, database, expectedLogin, passwordFile,
            Path.GetFullPath(migrationsDirectory), contract, timescaleVersion, uuidVersion,
            backupV1ValidUntil, loginVersion, sslMode, verifyOnly, cutover, adminFile,
            impactConfiguration, timeouts);
    }

    private NpgsqlConnectionStringBuilder Harden(NpgsqlConnectionStringBuilder builder)
    {
        builder.ApplicationName = "saydin-database-migrator";
        builder.Pooling = false;
        builder.IncludeErrorDetail = false;
        builder.LogParameters = false;
        builder.Passfile = null;
        builder.Options = null;
        builder.SearchPath = "pg_catalog,public,pg_temp";
        builder.Timeout = checked((int)Math.Ceiling(Timeouts.Connect.TotalSeconds));
        builder.CommandTimeout = checked((int)Math.Ceiling(Timeouts.Command.TotalSeconds));
        builder.CancellationTimeout = 1_000;
        return builder;
    }

    private static string Value(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string?> environment,
        string argument,
        string environmentKey,
        string? defaultValue = null)
    {
        var result = values.GetValueOrDefault(argument) ?? Get(environment, environmentKey) ?? defaultValue;
        if (string.IsNullOrWhiteSpace(result)) throw new MigratorRejectedException("argument_required", argument);
        return result;
    }

    private static TimeSpan Seconds(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string?> environment,
        string argument,
        string environmentKey,
        int defaultValue,
        int minimum,
        int maximum) => TimeSpan.FromSeconds(ParseInt(
            values.GetValueOrDefault(argument) ?? Get(environment, environmentKey) ??
            defaultValue.ToString(CultureInfo.InvariantCulture), minimum, maximum, "duration_invalid"));

    private static int ParseInt(string value, int minimum, int maximum, string code) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
        parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new MigratorRejectedException(code);

    private static string? Get(IReadOnlyDictionary<string, string?> environment, string key) =>
        environment.TryGetValue(key, out var value) ? value : null;
}
