using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Saydin.DatabaseRoleBootstrap;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseMigrator.Tests;

internal enum HbaBoundTestFixture
{
    BackupRotation,
    LegacyAck,
}

internal sealed record HbaBoundTestTarget(
    string Database,
    string DeploymentId,
    string RolePrefix);

internal static class IntegrationEnvironment
{
    public static string RequirePrimary() =>
        RequireConnectionString("SAYDIN_MIGRATOR_TEST_DATABASE_FILE");

    public static string RequireSecondary() =>
        RequireConnectionString("SAYDIN_MIGRATOR_SECONDARY_DATABASE_FILE");

    public static HbaBoundTestTarget RequireHbaBoundTarget(HbaBoundTestFixture fixture)
    {
        var runId = RequireValue("SAYDIN_INTEGRATION_RUN_ID");
        if (runId.Length != 32 || runId.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("SAYDIN_INTEGRATION_RUN_ID must be 32 lowercase hex characters.");
        var (label, deploymentSlug, environmentPrefix) = fixture switch
        {
            HbaBoundTestFixture.BackupRotation =>
                ("backup-rotation", "mbr", "SAYDIN_MIGRATOR_BACKUP_ROTATION"),
            HbaBoundTestFixture.LegacyAck =>
                ("legacy-ack", "mla", "SAYDIN_MIGRATOR_LEGACY_ACK"),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"saydin-migrator-hba-fixture/v1\0{runId}\0{label}")))[..32];
        var expectedDatabase = $"saydin_migrator_{suffix}";
        var expectedDeployment = $"{deploymentSlug}-{runId[..8]}";
        var database = RequireValue($"{environmentPrefix}_DATABASE");
        var deployment = RequireValue($"{environmentPrefix}_DEPLOYMENT_ID");
        var rolePrefix = RequireValue($"{environmentPrefix}_ROLE_PREFIX");
        if (!string.Equals(database, expectedDatabase, StringComparison.Ordinal) ||
            !string.Equals(deployment, expectedDeployment, StringComparison.Ordinal))
            throw new InvalidOperationException($"{environmentPrefix} target is not bound to this CI run.");
        return new HbaBoundTestTarget(database, deployment, rolePrefix);
    }

    private static string RequireConnectionString(string variable) =>
        SecureSecretFile.ReadConnectionString(RequireValue(variable));

    private static string RequireValue(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{variable} is required; real PostgreSQL tests never skip.");
    }
}

internal sealed class TestDatabase : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, TestDatabase> Registry =
        new(StringComparer.Ordinal);
    private readonly string _adminConnectionString;
    private readonly string _secretDirectory;
    private readonly IReadOnlyDictionary<LoginPurpose, string> _passwords;
    private readonly string _backupPassword;
    private bool _cleaned;

    private TestDatabase(
        string adminConnectionString,
        string name,
        string connectionString,
        string secretDirectory,
        RoleContract contract,
        string timescaleVersion,
        string uuidVersion,
        IReadOnlyDictionary<LoginPurpose, string> passwords,
        string backupPassword,
        DateTimeOffset backupV1ValidUntilUtc)
    {
        _adminConnectionString = adminConnectionString;
        _secretDirectory = secretDirectory;
        _passwords = passwords;
        _backupPassword = backupPassword;
        Name = name;
        ConnectionString = connectionString;
        Contract = contract;
        TimescaleVersion = timescaleVersion;
        UuidVersion = uuidVersion;
        BackupV1ValidUntilUtc = backupV1ValidUntilUtc;
    }

    public string Name { get; }
    public string ConnectionString { get; }
    public RoleContract Contract { get; }
    public string TimescaleVersion { get; }
    public string UuidVersion { get; }
    public DateTimeOffset BackupV1ValidUntilUtc { get; }
    public string BackupV1Password => _backupPassword;
    public string AdminConnectionFile => Path.Combine(_secretDirectory, "admin");
    public string MigratorPasswordFile => Path.Combine(_secretDirectory, "migrator-v1");

    public string WriteAdditionalSecret(string name, string value)
    {
        if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Invalid test secret name.", nameof(name));
        WriteSecret(name, value);
        return Path.Combine(_secretDirectory, name);
    }

    public static Task<TestDatabase> CreateAsync(string adminConnectionString) =>
        CreateAsync(adminConnectionString, target: null);

    public static Task<TestDatabase> CreateHbaBoundAsync(
        string adminConnectionString,
        HbaBoundTestFixture fixture) =>
        CreateAsync(adminConnectionString, IntegrationEnvironment.RequireHbaBoundTarget(fixture));

    private static async Task<TestDatabase> CreateAsync(
        string adminConnectionString,
        HbaBoundTestTarget? target)
    {
        var generatedSuffix = Guid.NewGuid().ToString("N");
        var name = target?.Database ?? $"saydin_migrator_{generatedSuffix}";
        ValidateName(name);
        var suffix = name["saydin_migrator_".Length..];
        var secretRoot = OperatingSystem.IsLinux() ? "/run/secrets" : Path.GetTempPath();
        Directory.CreateDirectory(secretRoot);
        var secretDirectory = Path.Combine(secretRoot, $"saydin-migrator-{suffix}");
        Directory.CreateDirectory(secretDirectory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(secretDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var clusterAdmin = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
            IncludeErrorDetail = false,
        };
        TestDatabase? database = null;
        try
        {
            await using var admin = new NpgsqlConnection(clusterAdmin.ConnectionString);
            await admin.OpenAsync();
            var systemIdentifier = Convert.ToString(await ScalarAsync(admin,
                "SELECT system_identifier::text FROM pg_catalog.pg_control_system()")) ??
                throw new InvalidOperationException("system identifier unavailable");
            var systemHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(systemIdentifier)));
            var timescale = Convert.ToString(await ScalarAsync(admin, """
                SELECT default_version FROM pg_catalog.pg_available_extensions WHERE name='timescaledb'
                """)) ?? throw new InvalidOperationException("timescaledb unavailable");
            var uuid = Convert.ToString(await ScalarAsync(admin, """
                SELECT default_version FROM pg_catalog.pg_available_extensions WHERE name='uuid-ossp'
                """)) ?? throw new InvalidOperationException("uuid-ossp unavailable");
            var deployment = target?.DeploymentId ?? $"mig-{suffix[..8]}";
            var derivedPrefix = RoleContract.DerivePrefix(deployment, name, systemHash);
            if (target is not null && !string.Equals(
                    target.RolePrefix, derivedPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("HBA-bound role prefix does not match the live target contract.");
            var contract = RoleContract.Create(
                deployment, name, systemHash, target?.RolePrefix ?? derivedPrefix);
            await ExecuteFormattedAsync(admin, "CREATE DATABASE %I TEMPLATE template0", name);

            var targetBuilder = new NpgsqlConnectionStringBuilder(clusterAdmin.ConnectionString)
            {
                Database = name,
                Pooling = false,
            };
            var passwords = Enum.GetValues<LoginPurpose>().ToDictionary(
                purpose => purpose,
                purpose => $"MIGRATOR-TEST-{RoleContract.PurposeName(purpose)}-{Guid.NewGuid():N}-A9!");
            var backupPassword = $"MIGRATOR-BACKUP-TEST-{Guid.NewGuid():N}-A9!";
            var backupV1ValidUntil = DateTimeOffset.UtcNow.AddDays(60);
            database = new TestDatabase(clusterAdmin.ConnectionString, name,
                targetBuilder.ConnectionString, secretDirectory, contract, timescale, uuid, passwords,
                backupPassword, backupV1ValidUntil);
            database.WriteSecret("admin", targetBuilder.ConnectionString);
            foreach (var (purpose, password) in passwords)
                database.WriteSecret($"{RoleContract.PurposeName(purpose)}-v1", password);
            database.WriteSecret("backup-v1", backupPassword);
            await database.BootstrapRolesAsync();
            if (!Registry.TryAdd(name, database))
                throw new InvalidOperationException("test database registry collision");
            return database;
        }
        catch
        {
            // Cleanup is best effort: a failure here must never replace the setup
            // exception that is the actual diagnostic.
            try
            {
                if (database is not null) await database.DisposeAsync();
                else
                {
                    await DropDatabaseBestEffortAsync(clusterAdmin.ConnectionString, name);
                    DeleteSecretDirectoryBestEffort(secretDirectory);
                }
            }
            catch (Exception cleanupFailure)
            {
                Console.Error.WriteLine($"test database cleanup failed: {cleanupFailure}");
            }
            throw;
        }
    }

    public static TestDatabase ForConnection(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        return database is not null && Registry.TryGetValue(database, out var registered)
            ? registered
            : throw new InvalidOperationException("unregistered migrator test database");
    }

    public MigratorOptions Options(
        string migrationsDirectory,
        bool legacyCutover = false,
        int loginVersion = 1,
        MigrationImpactConfiguration? impactConfiguration = null) => new(
        Host: new NpgsqlConnectionStringBuilder(ConnectionString).Host!,
        Port: new NpgsqlConnectionStringBuilder(ConnectionString).Port,
        Database: Name,
        ExpectedLogin: Contract.Login(LoginPurpose.Migrator, loginVersion).Name,
        PasswordFile: Path.Combine(_secretDirectory, $"migrator-v{loginVersion}"),
        MigrationsDirectory: migrationsDirectory,
        Contract: Contract,
        TimescaleVersion: TimescaleVersion,
        UuidOsspVersion: UuidVersion,
        BackupV1ValidUntilUtc: BackupV1ValidUntilUtc,
        LoginVersion: loginVersion,
        SslMode: new NpgsqlConnectionStringBuilder(ConnectionString).SslMode,
        VerifyOnly: false,
        LegacyPrivilegeCutover: legacyCutover,
        AdminConnectionFile: legacyCutover ? AdminConnectionFile : null,
        ImpactConfiguration: impactConfiguration,
        Timeouts: new MigratorTimeouts(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(25), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(7)));

    public IReadOnlyDictionary<string, string?> ApplicationEnvironment(
        bool verifyOnly = false,
        int loginVersion = 1)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        return new Dictionary<string, string?>
        {
            ["PGHOST"] = builder.Host,
            ["PGPORT"] = builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGDATABASE"] = Name,
            ["PGUSER"] = Contract.Login(LoginPurpose.Migrator, loginVersion).Name,
            ["PGSSLMODE"] = builder.SslMode.ToString(),
            ["SAYDIN_MIGRATOR_PASSWORD_FILE"] =
                Path.Combine(_secretDirectory, $"migrator-v{loginVersion}"),
            ["SAYDIN_MIGRATIONS_DIR"] = TestPaths.MigrationsDirectory,
            ["SAYDIN_DEPLOYMENT_ID"] = Contract.DeploymentId,
            ["SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256"] = Contract.SystemIdentifierSha256,
            ["SAYDIN_DATABASE_ROLE_PREFIX"] = Contract.Prefix,
            ["SAYDIN_TIMESCALEDB_VERSION"] = TimescaleVersion,
            ["SAYDIN_UUID_OSSP_VERSION"] = UuidVersion,
            ["SAYDIN_BACKUP_V1_VALID_UNTIL"] =
                RoleContract.FormatBackupValidUntil(BackupV1ValidUntilUtc),
            ["SAYDIN_MIGRATOR_LOGIN_VERSION"] =
                loginVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SAYDIN_MIGRATOR_LOCK_TIMEOUT_SECONDS"] = "20",
            ["SAYDIN_MIGRATOR_COMMAND_TIMEOUT_SECONDS"] = "300",
            ["SAYDIN_MIGRATOR_TOTAL_TIMEOUT_SECONDS"] = "420",
        };
    }

    public RuntimeDatabaseOptions RuntimeOptions(LoginPurpose purpose)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        return new RuntimeDatabaseOptions(
            purpose,
            Contract,
            Contract.Login(purpose, 1),
            builder.Host!,
            builder.Port,
            Name,
            builder.SslMode,
            Path.Combine(_secretDirectory, $"{RoleContract.PurposeName(purpose)}-v1"),
            RuntimeDatabasePooling.Disabled);
    }

    public async Task RotateMigratorV2Async()
    {
        var password = $"MIGRATOR-TEST-V2-{Guid.NewGuid():N}-A9!";
        var passwordFile = WriteAdditionalSecret("migrator-v2", password);
        var args = new[]
        {
            "rotate", "--admin-connection-file", AdminConnectionFile,
            "--deployment-id", Contract.DeploymentId,
            "--target-database", Name,
            "--system-identifier-sha256", Contract.SystemIdentifierSha256,
            "--role-prefix", Contract.Prefix,
            "--timescaledb-version", TimescaleVersion,
            "--uuid-ossp-version", UuidVersion,
            "--backup-v1-valid-until", RoleContract.FormatBackupValidUntil(BackupV1ValidUntilUtc),
            "--connect-timeout-seconds", "10", "--lock-timeout-seconds", "20",
            "--statement-timeout-seconds", "30", "--total-timeout-seconds", "90",
            "--login", "migrator", "--login-version", "2",
            "--password-file", passwordFile,
        };
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await BootstrapApplication.RunAsync(args, output, error);
        if (exit != BootstrapExitCodes.Success)
            throw new InvalidOperationException($"migrator v2 rotation failed: {error}");
    }

    public async Task RetireMigratorV1Async()
    {
        var args = new[]
        {
            "retire", "--admin-connection-file", AdminConnectionFile,
            "--deployment-id", Contract.DeploymentId,
            "--target-database", Name,
            "--system-identifier-sha256", Contract.SystemIdentifierSha256,
            "--role-prefix", Contract.Prefix,
            "--timescaledb-version", TimescaleVersion,
            "--uuid-ossp-version", UuidVersion,
            "--backup-v1-valid-until", RoleContract.FormatBackupValidUntil(BackupV1ValidUntilUtc),
            "--connect-timeout-seconds", "10", "--lock-timeout-seconds", "20",
            "--statement-timeout-seconds", "30", "--total-timeout-seconds", "90",
            "--login", "migrator", "--login-version", "1",
            "--replacement-version", "2", "--drain-timeout-seconds", "5",
        };
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await BootstrapApplication.RunAsync(args, output, error);
        if (exit != BootstrapExitCodes.Success)
            throw new InvalidOperationException($"migrator v1 retirement failed: {error}");
    }

    public async Task<(DateTimeOffset ValidUntilUtc, string Password)> RotateBackupV2Async()
    {
        var password = $"MIGRATOR-BACKUP-TEST-V2-{Guid.NewGuid():N}-A9!";
        var passwordFile = WriteAdditionalSecret("backup-v2", password);
        var validUntil = DateTimeOffset.UtcNow.AddDays(61);
        var args = new[]
        {
            "rotate", "--admin-connection-file", AdminConnectionFile,
            "--deployment-id", Contract.DeploymentId,
            "--target-database", Name,
            "--system-identifier-sha256", Contract.SystemIdentifierSha256,
            "--role-prefix", Contract.Prefix,
            "--timescaledb-version", TimescaleVersion,
            "--uuid-ossp-version", UuidVersion,
            "--backup-v1-valid-until", RoleContract.FormatBackupValidUntil(BackupV1ValidUntilUtc),
            "--connect-timeout-seconds", "10", "--lock-timeout-seconds", "20",
            "--statement-timeout-seconds", "30", "--total-timeout-seconds", "90",
            "--login", "backup", "--login-version", "2",
            "--password-file", passwordFile,
            "--valid-until", RoleContract.FormatBackupValidUntil(validUntil),
        };
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await BootstrapApplication.RunAsync(args, output, error);
        if (exit != BootstrapExitCodes.Success)
            throw new InvalidOperationException($"backup v2 rotation failed: {error}");
        if (output.ToString().Contains(password, StringComparison.Ordinal) ||
            error.ToString().Contains(password, StringComparison.Ordinal))
            throw new InvalidOperationException("backup secret appeared in role bootstrap output");
        return (validUntil, password);
    }

    public async Task EnsureRolesAsync()
    {
        var options = new BootstrapOptions(
            Command: BootstrapCommand.Ensure,
            AdminConnectionFile: AdminConnectionFile,
            Contract: Contract,
            TimescaleVersion: TimescaleVersion,
            UuidOsspVersion: UuidVersion,
            Timeouts: new BootstrapTimeouts(
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)),
            PasswordFiles: new Dictionary<LoginPurpose, string>(),
            BackupPasswordFile: Path.Combine(_secretDirectory, "backup-v1"),
            BackupV1ValidUntilUtc: BackupV1ValidUntilUtc,
            RotatePurpose: null,
            RotateBackup: false,
            RotateVersion: null,
            RotatePasswordFile: null,
            RotateBackupValidUntilUtc: null);
        var runner = new RoleBootstrapRunner(options, TextWriter.Null);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var backupVerifier = PostgresScramSha256Verifier.Create(
            Encoding.UTF8.GetBytes(_backupPassword));
        await runner.EnsureRoleAsync(
            connection, transaction, Contract.BackupLogin(1, BackupV1ValidUntilUtc),
            backupVerifier, CancellationToken.None, allowBackupValidityExtension: true);
        await transaction.CommitAsync();
    }

    public Task EnsureRolesThroughApplicationAsync() => BootstrapRolesAsync();

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync() ??
                   throw new InvalidOperationException("Expected scalar value."));
    }

    public async Task<(string Schema, string Table)> PriceChunkAsync(Guid assetId, DateOnly date)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT namespace.nspname,relation.relname
              FROM public.price_points point
              JOIN pg_catalog.pg_class relation ON relation.oid=point.tableoid
              JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
             WHERE point.asset_id=$1 AND point.price_date=$2
             LIMIT 1
            """, connection);
        command.Parameters.AddWithValue(assetId);
        command.Parameters.AddWithValue(date);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"Price chunk missing for asset {assetId:D}.");
        return (reader.GetString(0), reader.GetString(1));
    }

    public async Task WaitUntilReachableAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (attempt < 19)
            {
                await Task.Delay(50);
            }
        }
    }

    public async Task PrepareLegacy014Async()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var path in Directory.EnumerateFiles(TestPaths.MigrationsDirectory)
                     .Where(path => Path.GetExtension(path) == ".sql")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                     .TakeWhile(path => string.CompareOrdinal(
                         Path.GetFileName(path), "015_") < 0))
        {
            await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(path), connection)
            {
                CommandTimeout = 300,
            };
            await command.ExecuteNonQueryAsync();
        }

        if (Convert.ToInt64(await ScalarAsync(connection,
                "SELECT count(*) FROM public.schema_migrations"),
                System.Globalization.CultureInfo.InvariantCulture) != 16)
            throw new InvalidOperationException("Legacy 014 fixture did not reach the exact 16-row baseline.");
    }

    private async Task BootstrapRolesAsync()
    {
        var args = new List<string>
        {
            "ensure", "--admin-connection-file", AdminConnectionFile,
            "--deployment-id", Contract.DeploymentId,
            "--target-database", Name,
            "--system-identifier-sha256", Contract.SystemIdentifierSha256,
            "--role-prefix", Contract.Prefix,
            "--timescaledb-version", TimescaleVersion,
            "--uuid-ossp-version", UuidVersion,
            "--backup-v1-valid-until", RoleContract.FormatBackupValidUntil(BackupV1ValidUntilUtc),
            "--connect-timeout-seconds", "10", "--lock-timeout-seconds", "20",
            "--statement-timeout-seconds", "30", "--total-timeout-seconds", "90",
        };
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            args.Add($"--{RoleContract.PurposeName(purpose).Replace('_', '-')}-password-file");
            args.Add(Path.Combine(_secretDirectory, $"{RoleContract.PurposeName(purpose)}-v1"));
        }
        args.Add("--backup-password-file");
        args.Add(Path.Combine(_secretDirectory, "backup-v1"));
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await BootstrapApplication.RunAsync(args.ToArray(), output, error);
        if (exit != BootstrapExitCodes.Success)
            throw new InvalidOperationException($"role bootstrap test setup failed: {error}");
    }

    private void WriteSecret(string name, string value)
    {
        var path = Path.Combine(_secretDirectory, name);
        File.WriteAllText(path, value);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cleaned) return;
        ValidateName(Name);
        Registry.TryRemove(Name, out _);
        NpgsqlConnection.ClearAllPools();
        var failures = new List<Exception>();
        try { await DropDatabaseBestEffortAsync(_adminConnectionString, Name, throwOnFailure: true); }
        catch (Exception exception) { failures.Add(exception); }
        try
        {
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            var roles = Enum.GetValues<LoginPurpose>()
                .Select(purpose => Contract.Login(purpose, 2))
                .Concat([
                    Contract.BackupLogin(2, BackupV1ValidUntilUtc),
                    Contract.BackupLogin(1, BackupV1ValidUntilUtc),
                ])
                .Concat(Contract.AllRolesForVersion(1).Reverse());
            foreach (var role in roles)
            {
                await using var format = new NpgsqlCommand(
                    "SELECT pg_catalog.format('DROP ROLE IF EXISTS %I',$1)", admin);
                format.Parameters.AddWithValue(role.Name);
                var sql = Convert.ToString(await format.ExecuteScalarAsync())!;
                await using var drop = new NpgsqlCommand(sql, admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
        catch (Exception exception) { failures.Add(exception); }
        try { DeleteSecretDirectoryBestEffort(_secretDirectory, throwOnFailure: true); }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count != 0) throw new AggregateException(failures);
        _cleaned = true;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteFormattedAsync(
        NpgsqlConnection connection, string format, string identifier)
    {
        await using var formatter = new NpgsqlCommand("SELECT pg_catalog.format($1,$2)", connection);
        formatter.Parameters.AddWithValue(format);
        formatter.Parameters.AddWithValue(identifier);
        var sql = Convert.ToString(await formatter.ExecuteScalarAsync())!;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseBestEffortAsync(
        string adminConnectionString, string name, bool throwOnFailure = false)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Database = "postgres",
                Pooling = false,
            };
            await using var admin = new NpgsqlConnection(builder.ConnectionString);
            await admin.OpenAsync();
            await using (var terminate = new NpgsqlCommand("""
                SELECT pg_catalog.pg_terminate_backend(pid)
                  FROM pg_catalog.pg_stat_activity
                 WHERE datname=$1 AND pid<>pg_catalog.pg_backend_pid()
                """, admin))
            {
                terminate.Parameters.AddWithValue(name);
                await terminate.ExecuteNonQueryAsync();
            }
            await ExecuteFormattedAsync(admin, "DROP DATABASE IF EXISTS %I", name);
        }
        catch when (!throwOnFailure) { }
    }

    private static void DeleteSecretDirectoryBestEffort(string directory, bool throwOnFailure = false)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
        catch when (!throwOnFailure) { }
    }

    private static void ValidateName(string name)
    {
        if (!name.StartsWith("saydin_migrator_", StringComparison.Ordinal) ||
            name.Length != "saydin_migrator_".Length + 32 ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidOperationException("Unsafe ephemeral database name.");
    }
}
