using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.IntegrationTests;

internal sealed class RoleBootstrapPgHarness : IAsyncDisposable
{
    private const string AdminEnvironment = "SAYDIN_ROLE_BOOTSTRAP_TEST_ADMIN_CONNECTION_FILE";
    private readonly string adminConnection;
    private readonly string adminPassword;
    private readonly string directory;
    private readonly Dictionary<LoginPurpose, string> v1Passwords;
    private readonly string backupV1Password;
    private readonly HashSet<string> trackedPrefixes = new(StringComparer.Ordinal);
    private bool cleaned;

    internal static Action<SetupTargets>? AfterFirstDatabaseCreatedForTests { get; set; }
    internal static Action<string>? BeforeCleanupTargetForTests { get; set; }
    internal sealed record SetupTargets(
        string TargetDatabase, string SecondaryDatabase, string Prefix, string SecretDirectory);

    private RoleBootstrapPgHarness(
        string adminConnection,
        string directory,
        string targetDatabase,
        string secondaryDatabase,
        string deploymentId,
        string systemHash,
        string timescaleVersion,
        string uuidVersion,
        string adminPassword,
        Dictionary<LoginPurpose, string> v1Passwords,
        string backupV1Password,
        DateTimeOffset backupV1ValidUntilUtc)
    {
        this.adminConnection = adminConnection;
        this.directory = directory;
        TargetDatabase = targetDatabase;
        SecondaryDatabase = secondaryDatabase;
        DeploymentId = deploymentId;
        SystemHash = systemHash;
        TimescaleVersion = timescaleVersion;
        UuidVersion = uuidVersion;
        this.adminPassword = adminPassword;
        Contract = RoleContract.Create(deploymentId, targetDatabase, systemHash,
            RoleContract.DerivePrefix(deploymentId, targetDatabase, systemHash));
        trackedPrefixes.Add(Contract.Prefix);
        this.v1Passwords = v1Passwords;
        this.backupV1Password = backupV1Password;
        BackupV1ValidUntilUtc = backupV1ValidUntilUtc;
    }

    public string TargetDatabase { get; }
    public string SecondaryDatabase { get; }
    public string DeploymentId { get; }
    public string SystemHash { get; }
    public string TimescaleVersion { get; }
    public string UuidVersion { get; }
    public RoleContract Contract { get; }
    public DateTimeOffset BackupV1ValidUntilUtc { get; }
    internal string SecretDirectory => directory;

    public static async Task<RoleBootstrapPgHarness> CreateAsync(string? precreateExtension = null)
    {
        var configuredFile = Environment.GetEnvironmentVariable(AdminEnvironment);
        if (string.IsNullOrWhiteSpace(configuredFile))
            throw new InvalidOperationException($"{AdminEnvironment} is required; this suite never skips.");
        var configured = SecureSecretFile.ReadConnectionString(configuredFile);

        var admin = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false,
            IncludeErrorDetail = false,
            Timeout = 10,
            CommandTimeout = 30,
        };
        if (string.IsNullOrWhiteSpace(admin.Username) || string.IsNullOrWhiteSpace(admin.Password))
            throw new InvalidOperationException($"{AdminEnvironment} must contain admin credentials.");

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var target = $"saydin_role_{suffix}";
        var secondary = $"saydin_role_secondary_{suffix}";
        var deployment = $"itx-{suffix[..8]}";
        var secretRoot = OperatingSystem.IsLinux() ? "/run/secrets" : Path.GetTempPath();
        Directory.CreateDirectory(secretRoot);
        var directory = Path.Combine(secretRoot, $"saydin-role-bootstrap-{suffix}");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        var systemId = Convert.ToString(await ScalarAsync(connection,
            "SELECT system_identifier::text FROM pg_catalog.pg_control_system()")) ??
                       throw new InvalidOperationException("system identifier unavailable");
        var systemHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(systemId)));
        var timescale = Convert.ToString(await ScalarAsync(connection, """
            SELECT default_version FROM pg_catalog.pg_available_extensions WHERE name='timescaledb'
            """)) ?? throw new InvalidOperationException("timescaledb is unavailable");
        var uuid = Convert.ToString(await ScalarAsync(connection, """
            SELECT default_version FROM pg_catalog.pg_available_extensions WHERE name='uuid-ossp'
            """)) ?? throw new InvalidOperationException("uuid-ossp is unavailable");

        var passwords = Enum.GetValues<LoginPurpose>().ToDictionary(
            purpose => purpose,
            purpose => $"LOGIN-SENTINEL-{RoleContract.PurposeName(purpose)}-{Guid.NewGuid():N}-A9!");
        var backupPassword = $"BACKUP-SENTINEL-{Guid.NewGuid():N}-A9!";
        var backupValidUntil = DateTimeOffset.UtcNow.AddDays(60);
        var harness = new RoleBootstrapPgHarness(
            admin.ConnectionString, directory, target, secondary, deployment,
            systemHash, timescale, uuid, admin.Password, passwords,
            backupPassword, backupValidUntil);
        try
        {
            await ExecuteFormattedAsync(connection, "CREATE DATABASE %I TEMPLATE template0", target);
            AfterFirstDatabaseCreatedForTests?.Invoke(
                new SetupTargets(target, secondary, harness.Contract.Prefix, directory));
            await ExecuteFormattedAsync(connection, "CREATE DATABASE %I TEMPLATE template0", secondary);
            harness.Write("admin", harness.AdminFor(target));
            foreach (var (purpose, password) in passwords)
                harness.Write($"{RoleContract.PurposeName(purpose)}-v1", password);
            harness.Write("backup-v1", backupPassword);
            if (precreateExtension is not null)
                await harness.PrecreateExtensionAsync(precreateExtension);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public async Task<RunResult> RunEnsureAsync(
        IReadOnlyDictionary<LoginPurpose, string>? passwords = null,
        string? deployment = null,
        string? database = null,
        string? systemHash = null,
        string? prefix = null,
        string? adminFile = null,
        string? timescaleVersion = null,
        DateTimeOffset? backupV1ValidUntilUtc = null)
    {
        var actualPasswords = passwords ?? v1Passwords;
        var actualDeployment = deployment ?? DeploymentId;
        var actualDatabase = database ?? TargetDatabase;
        var actualHash = systemHash ?? SystemHash;
        var actualPrefix = prefix ?? RoleContract.DerivePrefix(
            actualDeployment, actualDatabase, actualHash);
        try
        {
            var validated = RoleContract.Create(
                actualDeployment, actualDatabase, actualHash, actualPrefix);
            lock (trackedPrefixes) trackedPrefixes.Add(validated.Prefix);
        }
        catch (DatabaseSecurityRejectedException)
        {
            // Invalid caller input is never promoted into a cleanup selector.
        }
        var args = CommonArgs("ensure", actualDeployment, actualDatabase, actualHash,
            actualPrefix, adminFile ?? Path.Combine(directory, "admin"), timescaleVersion,
            backupV1ValidUntilUtc).ToList();
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            var path = Write($"run-{Guid.NewGuid():N}-{RoleContract.PurposeName(purpose)}",
                actualPasswords[purpose]);
            args.Add($"--{RoleContract.PurposeName(purpose).Replace('_', '-')}-password-file");
            args.Add(path);
        }
        args.Add("--backup-password-file");
        args.Add(Write($"run-{Guid.NewGuid():N}-backup", backupV1Password));
        var result = await RunAsync(args.ToArray());
        AssertRedacted(result, actualPasswords.Values);
        return result;
    }

    public async Task<RunResult> RunVerifyAsync(
        string? deployment = null,
        DateTimeOffset? backupV1ValidUntilUtc = null)
    {
        var actualDeployment = deployment ?? DeploymentId;
        var prefix = RoleContract.DerivePrefix(actualDeployment, TargetDatabase, SystemHash);
        var result = await RunAsync(CommonArgs("verify", actualDeployment, TargetDatabase, SystemHash,
            prefix, Path.Combine(directory, "admin"),
            backupV1ValidUntilUtc: backupV1ValidUntilUtc));
        AssertRedacted(result, []);
        return result;
    }

    public async Task EnsureBackupRoleAsync(DateTimeOffset validUntilUtc)
    {
        var options = new BootstrapOptions(
            Command: BootstrapCommand.Ensure,
            AdminConnectionFile: Path.Combine(directory, "admin"),
            Contract: Contract,
            TimescaleVersion: TimescaleVersion,
            UuidOsspVersion: UuidVersion,
            Timeouts: new BootstrapTimeouts(
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)),
            PasswordFiles: new Dictionary<LoginPurpose, string>(),
            BackupPasswordFile: Path.Combine(directory, "backup-v1"),
            BackupV1ValidUntilUtc: validUntilUtc,
            RotatePurpose: null,
            RotateBackup: false,
            RotateVersion: null,
            RotatePasswordFile: null,
            RotateBackupValidUntilUtc: null);
        var runner = new RoleBootstrapRunner(options, TextWriter.Null);
        await using var connection = await OpenAdminAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await runner.EnsureRoleAsync(
            connection, transaction, Contract.BackupLogin(1, validUntilUtc),
            PostgresScramSha256Verifier.Create(Encoding.UTF8.GetBytes(backupV1Password)),
            CancellationToken.None, allowBackupValidityExtension: true);
        await transaction.CommitAsync();
    }

    public async Task<RunResult> RunRotateAsync(
        LoginPurpose purpose,
        string password,
        int version = 2)
    {
        var result = await RunAsync(CommonArgs("rotate", DeploymentId, TargetDatabase, SystemHash,
                Contract.Prefix, Path.Combine(directory, "admin"))
            .Concat(new[]
            {
                "--login", RoleContract.PurposeName(purpose).Replace('_', '-'),
                "--login-version", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--password-file", Write($"rotate-{Guid.NewGuid():N}", password),
            }).ToArray());
        AssertRedacted(result, [password]);
        return result;
    }

    public async Task<RunResult> RunResetPasswordAsync(
        LoginPurpose purpose,
        int version,
        string password)
    {
        var result = await RunAsync(CommonArgs(
                "reset-password", DeploymentId, TargetDatabase, SystemHash,
                Contract.Prefix, Path.Combine(directory, "admin"))
            .Concat(new[]
            {
                "--login", RoleContract.PurposeName(purpose).Replace('_', '-'),
                "--login-version", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--password-file", Write($"reset-{Guid.NewGuid():N}", password),
            }).ToArray());
        AssertRedacted(result, [password]);
        return result;
    }

    public async Task<RunResult> RunRetireAsync(
        LoginPurpose purpose,
        int retiredVersion,
        int replacementVersion,
        int drainTimeoutSeconds = 2)
    {
        var result = await RunAsync(CommonArgs(
                "retire", DeploymentId, TargetDatabase, SystemHash,
                Contract.Prefix, Path.Combine(directory, "admin"))
            .Concat(new[]
            {
                "--login", RoleContract.PurposeName(purpose).Replace('_', '-'),
                "--login-version", retiredVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "--replacement-version", replacementVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "--drain-timeout-seconds", drainTimeoutSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            }).ToArray());
        AssertRedacted(result, []);
        return result;
    }

    public async Task<NpgsqlConnection> OpenAdminAsync(string? database = null)
    {
        var connection = new NpgsqlConnection(AdminFor(database ?? TargetDatabase));
        await connection.OpenAsync();
        return connection;
    }

    public async Task<NpgsqlConnection> OpenLoginAsync(
        LoginPurpose purpose,
        int version,
        string? password = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminFor(TargetDatabase))
        {
            Username = Contract.Login(purpose, version).Name,
            Password = password ?? v1Passwords[purpose],
            Pooling = false,
            IncludeErrorDetail = false,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public string V1Password(LoginPurpose purpose) => v1Passwords[purpose];
    public IReadOnlyCollection<string> V1Passwords => v1Passwords.Values;

    public async Task<long> CountRolesAsync(string prefix)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_roles
             WHERE pg_catalog.left(rolname,pg_catalog.length($1)+1)=$1||'_'
            """, connection);
        command.Parameters.AddWithValue(prefix);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task<string> DatabaseOwnerAsync()
    {
        await using var connection = await OpenAdminAsync();
        return Convert.ToString(await ScalarAsync(connection, """
            SELECT pg_catalog.pg_get_userbyid(datdba)
              FROM pg_catalog.pg_database WHERE datname=current_database()
            """)) ?? throw new InvalidOperationException("database owner unavailable");
    }

    public string WriteAdminFor(string database, string name) => Write(name, AdminFor(database));

    public string WriteAdminForRole(string role, string password, string name)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminFor(TargetDatabase))
        {
            Username = role,
            Password = password,
        };
        return Write(name, builder.ConnectionString);
    }

    public async Task<string> SecondaryFingerprintAsync()
    {
        await using var connection = await OpenAdminAsync(SecondaryDatabase);
        return Convert.ToString(await ScalarAsync(connection, """
            SELECT pg_catalog.pg_get_userbyid(database.datdba) || '|' ||
                   coalesce(database.datacl::text,'NULL') || '|' ||
                   coalesce(namespace.nspacl::text,'NULL') || '|' ||
                   (SELECT count(*)::text FROM pg_catalog.pg_extension) || '|' ||
                   coalesce((SELECT proacl::text FROM pg_catalog.pg_proc
                              WHERE oid='pg_catalog.pg_control_system()'::pg_catalog.regprocedure),'NULL')
              FROM pg_catalog.pg_database database
              CROSS JOIN pg_catalog.pg_namespace namespace
             WHERE database.datname=current_database() AND namespace.nspname='public'
            """)) ?? throw new InvalidOperationException("secondary fingerprint unavailable");
    }

    public static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        return await command.ExecuteScalarAsync();
    }

    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<PostgresException> RejectsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task PrecreateExtensionAsync(string extension)
    {
        if (extension is not ("timescaledb" or "uuid-ossp"))
            throw new ArgumentOutOfRangeException(nameof(extension));
        var version = extension == "timescaledb" ? TimescaleVersion : UuidVersion;
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.format('CREATE EXTENSION %I WITH SCHEMA public VERSION %L',$1,$2)", connection);
        command.Parameters.AddWithValue(extension);
        command.Parameters.AddWithValue(version);
        var sql = Convert.ToString(await command.ExecuteScalarAsync()) ??
                  throw new InvalidOperationException("extension SQL unavailable");
        await ExecuteAsync(connection, sql);
    }

    private string[] CommonArgs(
        string command,
        string deployment,
        string database,
        string systemHash,
        string prefix,
        string adminFile,
        string? timescaleVersion = null,
        DateTimeOffset? backupV1ValidUntilUtc = null) =>
    [
        command,
        "--admin-connection-file", adminFile,
        "--deployment-id", deployment,
        "--target-database", database,
        "--system-identifier-sha256", systemHash,
        "--role-prefix", prefix,
        "--timescaledb-version", timescaleVersion ?? TimescaleVersion,
        "--uuid-ossp-version", UuidVersion,
        "--backup-v1-valid-until", RoleContract.FormatBackupValidUntil(
            backupV1ValidUntilUtc ?? BackupV1ValidUntilUtc),
        "--connect-timeout-seconds", "5",
        "--lock-timeout-seconds", "20",
        "--statement-timeout-seconds", "30",
        "--total-timeout-seconds", "90",
    ];

    private static async Task<RunResult> RunAsync(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await BootstrapApplication.RunAsync(args, output, error);
        return new RunResult(exitCode, output.ToString(), error.ToString());
    }

    private void AssertRedacted(RunResult result, IEnumerable<string> additionalSecrets)
    {
        var text = result.Output + result.Error;
        Assert.DoesNotContain(adminPassword, text, StringComparison.Ordinal);
        foreach (var secret in v1Passwords.Values.Concat(additionalSecrets))
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    private string AdminFor(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Database = database,
            Pooling = false,
            IncludeErrorDetail = false,
            Timeout = 10,
            CommandTimeout = 30,
        };
        return builder.ConnectionString;
    }

    private string Write(string name, string value)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, value);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static async Task ExecuteFormattedAsync(
        NpgsqlConnection connection,
        string format,
        string identifier)
    {
        await using var formatCommand = new NpgsqlCommand(
            "SELECT pg_catalog.format($1,$2)", connection);
        formatCommand.Parameters.AddWithValue(format);
        formatCommand.Parameters.AddWithValue(identifier);
        var sql = Convert.ToString(await formatCommand.ExecuteScalarAsync()) ??
                  throw new InvalidOperationException("formatted SQL unavailable");
        await ExecuteAsync(connection, sql);
    }

    public async ValueTask DisposeAsync()
    {
        if (cleaned) return;
        var failures = new List<Exception>();
        await using var connection = new NpgsqlConnection(adminConnection);
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (connection.State == System.Data.ConnectionState.Open)
        {
            foreach (var database in new[] { TargetDatabase, SecondaryDatabase })
            {
                try
                {
                    BeforeCleanupTargetForTests?.Invoke($"database:{database}");
                    await using var command = new NpgsqlCommand(
                        "SELECT pg_catalog.format('DROP DATABASE IF EXISTS %I WITH (FORCE)',$1)", connection);
                    command.Parameters.AddWithValue(database);
                    var sql = Convert.ToString(await command.ExecuteScalarAsync());
                    if (sql is not null) await ExecuteAsync(connection, sql);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                BeforeCleanupTargetForTests?.Invoke("roles");
                string[] prefixes;
                lock (trackedPrefixes) prefixes = trackedPrefixes.ToArray();
                await using var inspect = new NpgsqlCommand("""
                    SELECT rolname FROM pg_catalog.pg_roles
                     WHERE EXISTS (
                         SELECT 1 FROM pg_catalog.unnest($1::text[]) prefix
                          WHERE pg_catalog.left(rolname,pg_catalog.length(prefix)+1)=prefix||'_')
                     ORDER BY rolcanlogin DESC, rolname COLLATE "C"
                    """, connection);
                inspect.Parameters.AddWithValue(prefixes);
                var roles = new List<string>();
                await using (var reader = await inspect.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) roles.Add(reader.GetString(0));
                foreach (var role in roles)
                    await ExecuteFormattedAsync(connection, "DROP ROLE IF EXISTS %I", role);
                await using var remainingCommand = new NpgsqlCommand("""
                    SELECT count(*) FROM pg_catalog.pg_roles
                     WHERE EXISTS (
                         SELECT 1 FROM pg_catalog.unnest($1::text[]) prefix
                          WHERE pg_catalog.left(rolname,pg_catalog.length(prefix)+1)=prefix||'_')
                    """, connection);
                remainingCommand.Parameters.AddWithValue(prefixes);
                if (Convert.ToInt64(await remainingCommand.ExecuteScalarAsync()) != 0)
                    throw new InvalidOperationException("managed role cleanup incomplete");
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            BeforeCleanupTargetForTests?.Invoke("secret-directory");
            var allowedRoot = OperatingSystem.IsLinux() ? "/run/secrets/" : Path.GetTempPath();
            if (directory.StartsWith(allowedRoot, StringComparison.Ordinal) && Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(
                             directory, "*", SearchOption.TopDirectoryOnly))
                    File.Delete(path);
                Directory.Delete(directory);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count != 0) throw new AggregateException("role bootstrap cleanup incomplete", failures);
        cleaned = true;
    }
}

internal sealed record RunResult(int ExitCode, string Output, string Error)
{
    public void AssertSuccess()
    {
        Assert.True(ExitCode == BootstrapExitCodes.Success,
            $"expected success; exit={ExitCode}; output={Output}; error={Error}");
        Assert.Equal(string.Empty, Error);
        Assert.Contains("contract_sha256=", Output, StringComparison.Ordinal);
    }

    public void AssertFailure(int exitCode, string stableCode)
    {
        Assert.Equal(exitCode, ExitCode);
        Assert.Equal(string.Empty, Output);
        Assert.Equal($"role-bootstrap failed: code={stableCode}{Environment.NewLine}", Error);
    }
}
