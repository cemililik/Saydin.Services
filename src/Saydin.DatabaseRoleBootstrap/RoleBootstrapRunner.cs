using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Npgsql.Replication;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap;

internal sealed partial class RoleBootstrapRunner(BootstrapOptions options, TextWriter output)
{
    private static readonly IReadOnlySet<LoginPurpose> PublicSchemaUsers =
        new HashSet<LoginPurpose>
        {
            LoginPurpose.Api,
            LoginPurpose.Ingestion,
            LoginPurpose.CalendarImporter,
            LoginPurpose.Audit,
        };

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var adminSecret = SecureSecretFile.ReadConnectionString(options.AdminConnectionFile);
            var secrets = LoadPasswordInputs();
            var adminBuilder = BuildAdminConnection(adminSecret);

            await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await ConfigureSessionAsync(connection, cancellationToken);
            var adminTarget = await VerifyTargetAsync(connection, cancellationToken);
            var lockKey = ContractLockKey(options.Contract.TargetLockSha256);
            await AcquireLockAsync(connection, lockKey, cancellationToken);
            try
            {
                switch (options.Command)
                {
                    case BootstrapCommand.Ensure:
                        var backupManaged = await EnsureAsync(
                            connection, adminTarget.Role, secrets.LoginPasswords,
                            secrets.BackupPassword ?? throw InvalidState(), cancellationToken);
                        await AuthenticateAsync(adminBuilder, Enum.GetValues<LoginPurpose>()
                            .ToDictionary(
                                purpose => options.Contract.Login(purpose, 1),
                                purpose => secrets.LoginPasswords[purpose]), adminTarget, cancellationToken);
                        if (backupManaged)
                            await AuthenticateBackupAsync(
                                adminBuilder,
                                options.Contract.BackupLogin(1, options.BackupV1ValidUntilUtc),
                                secrets.BackupPassword, cancellationToken);
                        await RejectSchedulerAuthenticationAsync(adminBuilder, cancellationToken);
                        await output.WriteLineAsync(
                            $"role-bootstrap ensure complete: contract_sha256={options.ContractSha256}; " +
                            $"backup_contract_sha256={options.Contract.BackupContractSha256(options.TimescaleVersion, options.UuidOsspVersion, options.BackupV1ValidUntilUtc)}; " +
                            $"login_version=1; backup_login_version={(backupManaged ? "1" : "pending")}; " +
                            $"backup_postbootstrap_required={(!backupManaged).ToString().ToLowerInvariant()}");
                        break;

                    case BootstrapCommand.Verify:
                        await VerifyAsync(connection, adminTarget.Role, cancellationToken);
                        await output.WriteLineAsync(
                            $"role-bootstrap verify complete: contract_sha256={options.ContractSha256}");
                        break;

                    case BootstrapCommand.Rotate:
                        var version = options.RotateVersion ?? throw InvalidState();
                        if (options.RotateBackup)
                        {
                            var backupPassword = secrets.BackupPassword ?? throw InvalidState();
                            var backup = await RotateBackupAsync(
                                connection, adminTarget.Role, version, backupPassword,
                                options.RotateBackupValidUntilUtc ?? throw InvalidState(), cancellationToken);
                            await AuthenticateBackupAsync(
                                adminBuilder, backup, backupPassword, cancellationToken);
                            await output.WriteLineAsync(
                                $"role-bootstrap rotate complete: contract_sha256={options.ContractSha256}; " +
                                $"backup_contract_sha256={options.Contract.BackupContractSha256(options.TimescaleVersion, options.UuidOsspVersion, options.BackupV1ValidUntilUtc)}; " +
                                $"login=backup; login_version={version}");
                        }
                        else
                        {
                            var purpose = options.RotatePurpose ?? throw InvalidState();
                            var password = secrets.LoginPasswords[purpose];
                            var login = await RotateAsync(
                                connection, adminTarget.Role, purpose, version, password, cancellationToken);
                            await AuthenticateAsync(adminBuilder,
                                new Dictionary<ManagedRole, string> { [login] = password },
                                adminTarget, cancellationToken);
                            await output.WriteLineAsync(
                                $"role-bootstrap rotate complete: contract_sha256={options.ContractSha256}; " +
                                $"login={RoleContract.PurposeName(purpose)}; login_version={version}");
                        }
                        break;

                    default:
                        throw InvalidState();
                }
            }
            finally
            {
                await ReleaseLockBestEffortAsync(connection, lockKey, cancellationToken);
            }
        }
        catch (BootstrapRejectedException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgresException exception)
        {
            if (exception.SqlState is "55P03" or "57014")
                throw new BootstrapRejectedException("database_timeout", BootstrapExitCodes.Timeout);
            throw new BootstrapRejectedException(
                DatabaseCode(exception.SqlState), BootstrapExitCodes.DatabaseFailure);
        }
        catch (NpgsqlException)
        {
            throw new BootstrapRejectedException(
                "database_transport_failed", BootstrapExitCodes.DatabaseFailure);
        }
    }

    private LoadedSecrets LoadPasswordInputs()
    {
        var result = new Dictionary<LoginPurpose, string>();
        string? backupPassword = null;
        if (options.Command == BootstrapCommand.Ensure)
        {
            foreach (var (purpose, path) in options.PasswordFiles)
                result.Add(purpose, SecureSecretFile.ReadPassword(path));
            backupPassword = SecureSecretFile.ReadPassword(
                options.BackupPasswordFile ?? throw InvalidState());
        }
        else if (options.Command == BootstrapCommand.Rotate)
        {
            var password = SecureSecretFile.ReadPassword(
                options.RotatePasswordFile ?? throw InvalidState());
            if (options.RotateBackup)
                backupPassword = password;
            else
                result.Add(options.RotatePurpose ?? throw InvalidState(), password);
        }
        return new LoadedSecrets(result, backupPassword);
    }

    internal NpgsqlConnectionStringBuilder BuildAdminConnection(string secret)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(secret);
        }
        catch (ArgumentException)
        {
            throw new BootstrapRejectedException(
                "admin_connection_secret_invalid", BootstrapExitCodes.SecretRejected);
        }

        if (string.IsNullOrWhiteSpace(builder.Host) || builder.Host.Contains(',', StringComparison.Ordinal) ||
            builder.Host.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(builder.Username) ||
            string.IsNullOrWhiteSpace(builder.Password) ||
            !string.Equals(builder.Database, options.Contract.Database, StringComparison.Ordinal) ||
            builder.Database is "postgres" or "template0" or "template1" ||
            builder.LoadBalanceHosts ||
            (!string.IsNullOrEmpty(builder.TargetSessionAttributes) &&
             !string.Equals(builder.TargetSessionAttributes, "any", StringComparison.OrdinalIgnoreCase)) ||
            builder.Multiplexing || !string.IsNullOrEmpty(builder.Options) ||
            !string.IsNullOrEmpty(builder.Passfile) || !string.IsNullOrEmpty(builder.SearchPath))
        {
            throw new BootstrapRejectedException(
                "admin_connection_target_mismatch", BootstrapExitCodes.TargetRejected);
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = builder.Host,
            Port = builder.Port,
            Database = builder.Database,
            Username = builder.Username,
            Password = builder.Password,
            SslMode = builder.SslMode,
            RootCertificate = builder.RootCertificate,
            SslCertificate = builder.SslCertificate,
            SslKey = builder.SslKey,
            CheckCertificateRevocation = builder.CheckCertificateRevocation,
            ApplicationName = "saydin-database-role-bootstrap",
            Pooling = false,
            IncludeErrorDetail = false,
            LogParameters = false,
            Timeout = checked((int)Math.Ceiling(options.Timeouts.Connect.TotalSeconds)),
            CommandTimeout = checked((int)Math.Ceiling(options.Timeouts.Statement.TotalSeconds)),
            CancellationTimeout = 1_000,
            SearchPath = "pg_catalog,public,pg_temp",
        };
    }

    private async Task<VerifiedAdminTarget> VerifyTargetAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT current_database(), session_user, current_user,
                   role.rolsuper, role.rolcreaterole, role.rolcreatedb,
                   role.oid, current_setting('search_path'),
                   current_setting('server_version_num')::integer,
                   system_identifier::text,
                   coalesce(pg_catalog.inet_server_addr()::text,'local'),
                   coalesce(pg_catalog.inet_server_port(),0)
              FROM pg_catalog.pg_roles role
              CROSS JOIN pg_catalog.pg_control_system()
             WHERE role.rolname = session_user
            """, connection)
        {
            CommandTimeout = Seconds(options.Timeouts.Statement),
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw TargetRejected("admin_identity_unavailable");

        var database = reader.GetString(0);
        var sessionUser = reader.GetString(1);
        var currentUser = reader.GetString(2);
        var bootstrapRoleOid = reader.GetFieldValue<uint>(6);
        var searchPath = reader.GetString(7);
        var serverVersion = reader.GetInt32(8);
        var systemIdentifier = reader.GetString(9);
        var serverAddress = reader.GetString(10);
        var serverPort = reader.GetInt32(11);
        if (!string.Equals(database, options.Contract.Database, StringComparison.Ordinal) ||
            !string.Equals(sessionUser, currentUser, StringComparison.Ordinal) ||
            !reader.GetBoolean(3) || !reader.GetBoolean(4) || !reader.GetBoolean(5) ||
            bootstrapRoleOid != 10 || searchPath != "pg_catalog,public,pg_temp" ||
            serverVersion is < 160000 or >= 170000)
        {
            throw TargetRejected("admin_or_server_contract_rejected");
        }

        if (await reader.ReadAsync(cancellationToken))
            throw TargetRejected("admin_identity_unavailable");

        var actualHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(systemIdentifier)));
        if (!CryptographicEquals(actualHash, options.Contract.SystemIdentifierSha256))
            throw TargetRejected("target_system_identifier_mismatch");
        return new VerifiedAdminTarget(sessionUser, serverAddress, serverPort);
    }

    private async Task ConfigureSessionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.set_config('search_path', 'pg_catalog,public,pg_temp', false),
                   pg_catalog.set_config('password_encryption', 'scram-sha-256', false),
                   pg_catalog.set_config('lock_timeout', $1, false),
                   pg_catalog.set_config('statement_timeout', $2, false),
                   pg_catalog.set_config('idle_in_transaction_session_timeout', $3, false)
            """, connection)
        {
            CommandTimeout = Seconds(options.Timeouts.Statement),
        };
        command.Parameters.AddWithValue($"{Math.Ceiling(options.Timeouts.Lock.TotalMilliseconds)}ms");
        command.Parameters.AddWithValue($"{Math.Ceiling(options.Timeouts.Statement.TotalMilliseconds)}ms");
        command.Parameters.AddWithValue($"{Math.Ceiling(options.Timeouts.Total.TotalMilliseconds)}ms");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AcquireLockAsync(
        NpgsqlConnection connection,
        long lockKey,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < options.Timeouts.Lock)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_catalog.pg_try_advisory_lock($1)", connection)
            {
                CommandTimeout = Seconds(options.Timeouts.Statement),
            };
            command.Parameters.AddWithValue(lockKey);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        throw new BootstrapRejectedException("role_bootstrap_lock_timeout", BootstrapExitCodes.Timeout);
    }

    private async Task ReleaseLockBestEffortAsync(
        NpgsqlConnection connection,
        long lockKey,
        CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open || cancellationToken.IsCancellationRequested)
            return;
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_catalog.pg_advisory_unlock($1)", connection)
            {
                CommandTimeout = 5,
            };
            command.Parameters.AddWithValue(lockKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The original result remains authoritative. No server text is emitted.
        }
    }

    private async Task<bool> EnsureAsync(
        NpgsqlConnection connection,
        string adminRole,
        IReadOnlyDictionary<LoginPurpose, string> passwords,
        string backupPassword,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetLocalTimeoutsAsync(connection, transaction, cancellationToken);
        var backupPhaseReady = await IsBackupPhaseReadyAsync(
            connection, transaction, cancellationToken);
        var backup = options.Contract.BackupLogin(1, options.BackupV1ValidUntilUtc);
        var expectedRoles = backupPhaseReady
            ? options.Contract.AllRolesForVersion(1).Append(backup).ToArray()
            : options.Contract.AllRolesForVersion(1);
        await RejectRoleCollisionsAsync(connection, transaction, expectedRoles, cancellationToken,
            allowKnownRotations: true, allowBackupRoles: backupPhaseReady);
        var ownerAlreadyExists = await ReadRoleAsync(
            connection, transaction, options.Contract.Owner.Name, cancellationToken) is not null;
        await EnsureExtensionsAsync(connection, transaction, adminRole, cancellationToken);

        foreach (var role in options.Contract.StableRoles)
            await EnsureRoleAsync(connection, transaction, role, password: null, cancellationToken);
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
            await EnsureRoleAsync(connection, transaction, options.Contract.Login(purpose, 1),
                passwords[purpose], cancellationToken);
        if (backupPhaseReady)
        {
            if (await ReadRoleAsync(connection, transaction, backup.Name, cancellationToken) is null)
                await ValidateNewBackupValidityAsync(
                    connection, transaction, backup.ValidUntilUtc!.Value,
                    requireV1Overlap: false, cancellationToken);
            await EnsureRoleAsync(connection, transaction, backup, backupPassword, cancellationToken);
        }

        await EnsureMembershipsAsync(connection, transaction, cancellationToken);
        await EnsureDatabaseControlPlaneAsync(
            connection, transaction, adminRole, ownerAlreadyExists, cancellationToken);
        await EnsureTimescaleTransitionControlPlaneAsync(
            connection, transaction, adminRole, cancellationToken);
        await EnsurePrincipalRetentionTransitionControlPlaneAsync(
            connection, transaction, adminRole, cancellationToken);
        await VerifyContractAsync(
            connection, transaction, adminRole, backupPhaseReady, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return backupPhaseReady;
    }

    private async Task VerifyAsync(
        NpgsqlConnection connection,
        string adminRole,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        await SetLocalTimeoutsAsync(connection, transaction, cancellationToken);
        var backupPhaseReady = await IsBackupPhaseReadyAsync(
            connection, transaction, cancellationToken);
        var expectedRoles = backupPhaseReady
            ? options.Contract.AllRolesForVersion(1).Append(
                options.Contract.BackupLogin(1, options.BackupV1ValidUntilUtc)).ToArray()
            : options.Contract.AllRolesForVersion(1);
        await RejectRoleCollisionsAsync(connection, transaction, expectedRoles,
            cancellationToken, allowKnownRotations: true, allowBackupRoles: backupPhaseReady);
        await VerifyContractAsync(
            connection, transaction, adminRole, backupPhaseReady, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
    }

    private async Task<ManagedRole> RotateAsync(
        NpgsqlConnection connection,
        string adminRole,
        LoginPurpose purpose,
        int version,
        string password,
        CancellationToken cancellationToken)
    {
        var login = options.Contract.Login(purpose, version);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetLocalTimeoutsAsync(connection, transaction, cancellationToken);
        var backupPhaseReady = await IsBackupPhaseReadyAsync(
            connection, transaction, cancellationToken);
        var expectedRoles = backupPhaseReady
            ? options.Contract.AllRolesForVersion(1).Append(
                options.Contract.BackupLogin(1, options.BackupV1ValidUntilUtc)).Append(login).ToArray()
            : options.Contract.AllRolesForVersion(1).Append(login).ToArray();
        await RejectRoleCollisionsAsync(connection, transaction,
            expectedRoles, cancellationToken, allowKnownRotations: true,
            allowBackupRoles: backupPhaseReady);
        await VerifyContractAsync(
            connection, transaction, adminRole, backupPhaseReady, cancellationToken);
        await EnsureRoleAsync(connection, transaction, login, password, cancellationToken);
        await EnsureMembershipForLoginAsync(connection, transaction, purpose, login, cancellationToken);
        await VerifyContractAsync(
            connection, transaction, adminRole, backupPhaseReady, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return login;
    }

    private async Task<ManagedRole> RotateBackupAsync(
        NpgsqlConnection connection,
        string adminRole,
        int version,
        string password,
        DateTimeOffset validUntilUtc,
        CancellationToken cancellationToken)
    {
        var v1 = options.Contract.BackupLogin(1, options.BackupV1ValidUntilUtc);
        var login = options.Contract.BackupLogin(version, validUntilUtc);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetLocalTimeoutsAsync(connection, transaction, cancellationToken);
        if (!await IsBackupPhaseReadyAsync(connection, transaction, cancellationToken))
            throw TopologyRejected("backup_postbootstrap_required");
        await RejectRoleCollisionsAsync(connection, transaction,
            [.. options.Contract.AllRolesForVersion(1), v1, login],
            cancellationToken, allowKnownRotations: true);
        await VerifyContractAsync(connection, transaction, adminRole, requireBackup: true, cancellationToken);
        if (await ReadRoleAsync(connection, transaction, login.Name, cancellationToken) is null)
            await ValidateNewBackupValidityAsync(
                connection, transaction, validUntilUtc, requireV1Overlap: true, cancellationToken);
        await EnsureRoleAsync(connection, transaction, login, password, cancellationToken);
        await VerifyContractAsync(connection, transaction, adminRole, requireBackup: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return login;
    }

    private async Task SetLocalTimeoutsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.set_config('lock_timeout', $1, true),
                   pg_catalog.set_config('statement_timeout', $2, true),
                   pg_catalog.set_config('idle_in_transaction_session_timeout', $3, true)
            """, connection, transaction)
        {
            CommandTimeout = Seconds(options.Timeouts.Statement),
        };
        command.Parameters.AddWithValue($"{Math.Ceiling(options.Timeouts.Lock.TotalMilliseconds)}ms");
        command.Parameters.AddWithValue($"{Math.Ceiling(options.Timeouts.Statement.TotalMilliseconds)}ms");
        command.Parameters.AddWithValue($"{Math.Ceiling(options.Timeouts.Total.TotalMilliseconds)}ms");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RejectRoleCollisionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyCollection<ManagedRole> expectedRoles,
        CancellationToken cancellationToken,
        bool allowKnownRotations = false,
        bool allowBackupRoles = true)
    {
        var expected = expectedRoles.ToDictionary(role => role.Name, StringComparer.Ordinal);
        {
            await using var command = new NpgsqlCommand("""
                SELECT role.rolname,
                       pg_catalog.shobj_description(role.oid, 'pg_authid')
                  FROM pg_catalog.pg_roles role
                 WHERE pg_catalog.left(role.rolname, pg_catalog.length($1) + 1) = $1 || '_'
                 ORDER BY role.rolname COLLATE "C"
                """, connection, transaction)
            {
                CommandTimeout = Seconds(options.Timeouts.Statement),
            };
            command.Parameters.AddWithValue(options.Contract.Prefix);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var marker = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (expected.TryGetValue(name, out var role))
                {
                    if (!string.Equals(marker, role.Marker, StringComparison.Ordinal))
                        throw RoleCollision();
                    continue;
                }

                if (allowKnownRotations && marker is not null &&
                    options.Contract.TryResolveManagedMarker(marker, out var resolved) &&
                    resolved.LoginVersion == 2 &&
                    (allowBackupRoles || resolved.Purpose != "backup") &&
                    string.Equals(name, resolved.Name, StringComparison.Ordinal))
                    continue;

                throw RoleCollision();
            }
        }

        foreach (var role in expectedRoles)
        {
            await using var exact = new NpgsqlCommand("""
                SELECT pg_catalog.shobj_description(role.oid, 'pg_authid')
                  FROM pg_catalog.pg_roles role WHERE role.rolname=$1
                """, connection, transaction);
            exact.Parameters.AddWithValue(role.Name);
            var marker = await exact.ExecuteScalarAsync(cancellationToken);
            if (marker is not null && !string.Equals(Convert.ToString(marker, CultureInfo.InvariantCulture),
                    role.Marker, StringComparison.Ordinal))
                throw RoleCollision();
        }
    }

    private static BootstrapRejectedException RoleCollision() =>
        new("managed_role_name_collision", BootstrapExitCodes.RoleCollision);

    private static long ContractLockKey(string contractHash) =>
        unchecked((long)Convert.ToUInt64(contractHash[..16], 16));

    private static int Seconds(TimeSpan value) =>
        Math.Max(1, checked((int)Math.Ceiling(value.TotalSeconds)));

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static LoginPurpose ParsePurpose(string purpose) => purpose switch
    {
        "migrator" => LoginPurpose.Migrator,
        "api" => LoginPurpose.Api,
        "ingestion" => LoginPurpose.Ingestion,
        "calendar_importer" => LoginPurpose.CalendarImporter,
        "exporter" => LoginPurpose.Exporter,
        "audit" => LoginPurpose.Audit,
        _ => throw InvalidState(),
    };

    private static BootstrapRejectedException TargetRejected(string code) =>
        new(code, BootstrapExitCodes.TargetRejected);

    private static BootstrapRejectedException InvalidState() =>
        new("internal_contract_invalid", BootstrapExitCodes.TopologyRejected);

    private static string DatabaseCode(string sqlState) => sqlState switch
    {
        "42501" => "admin_privilege_insufficient",
        "3D000" => "target_database_missing",
        _ => "database_operation_failed",
    };

    private sealed record LoadedSecrets(
        IReadOnlyDictionary<LoginPurpose, string> LoginPasswords,
        string? BackupPassword);
}
