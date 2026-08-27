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
            using var secrets = LoadPasswordInputs();
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
                        var ensureResult = await EnsureAsync(
                            connection, adminTarget.Role, secrets.LoginPasswords,
                            secrets.BackupPassword ?? throw InvalidState(), cancellationToken);
                        await AuthenticateAsync(
                            adminBuilder, ensureResult.CurrentLogins, adminTarget, cancellationToken);
                        if (ensureResult.BackupManaged)
                            await AuthenticateBackupAsync(
                                adminBuilder,
                                options.Contract.BackupLogin(1, options.BackupV1ValidUntilUtc),
                                secrets.BackupPassword, cancellationToken);
                        await RejectSchedulerAuthenticationAsync(adminBuilder, cancellationToken);
                        await output.WriteLineAsync(
                            $"role-bootstrap ensure complete: contract_sha256={options.ContractSha256}; " +
                            $"backup_contract_sha256={options.Contract.BackupContractSha256(options.TimescaleVersion, options.UuidOsspVersion, options.BackupV1ValidUntilUtc)}; " +
                            $"login_versions={FormatLoginVersions(ensureResult.CurrentLogins.Keys)}; " +
                            $"backup_login_version={(ensureResult.BackupManaged ? "1" : "pending")}; " +
                            $"backup_postbootstrap_required={(!ensureResult.BackupManaged).ToString().ToLowerInvariant()}");
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
                                connection, adminTarget.Role, version,
                                backupPassword.CreateVerifier(),
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
                                connection, adminTarget.Role, purpose, version,
                                password.CreateVerifier(), cancellationToken);
                            await AuthenticateAsync(adminBuilder,
                                new Dictionary<ManagedRole, SensitivePassword> { [login] = password },
                                adminTarget, cancellationToken);
                            await output.WriteLineAsync(
                                $"role-bootstrap rotate complete: contract_sha256={options.ContractSha256}; " +
                                $"login={RoleContract.PurposeName(purpose)}; login_version={version}");
                        }
                        break;

                    case BootstrapCommand.ResetPassword:
                        var resetPurpose = options.RotatePurpose ?? throw InvalidState();
                        var resetVersion = options.RotateVersion ?? throw InvalidState();
                        var resetPassword = secrets.LoginPasswords[resetPurpose];
                        var resetLogin = await ResetPasswordAsync(
                            connection, adminTarget.Role, resetPurpose, resetVersion,
                            resetPassword.CreateVerifier(), cancellationToken);
                        await AuthenticateAsync(adminBuilder,
                            new Dictionary<ManagedRole, SensitivePassword>
                            {
                                [resetLogin] = resetPassword,
                            }, adminTarget, cancellationToken);
                        await output.WriteLineAsync(
                            $"role-bootstrap reset-password complete: contract_sha256={options.ContractSha256}; " +
                            $"login={RoleContract.PurposeName(resetPurpose)}; login_version={resetVersion}");
                        break;

                    case BootstrapCommand.Retire:
                        var retirePurpose = options.RotatePurpose ?? throw InvalidState();
                        var retiredVersion = options.RotateVersion ?? throw InvalidState();
                        var replacementVersion = options.ReplacementVersion ?? throw InvalidState();
                        await RetireAsync(
                            connection, adminTarget.Role, retirePurpose, retiredVersion,
                            replacementVersion, options.DrainTimeout ?? throw InvalidState(),
                            cancellationToken);
                        await output.WriteLineAsync(
                            $"role-bootstrap retire complete: contract_sha256={options.ContractSha256}; " +
                            $"login={RoleContract.PurposeName(retirePurpose)}; " +
                            $"retired_version={retiredVersion}; current_version={replacementVersion}");
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
        var result = new Dictionary<LoginPurpose, SensitivePassword>();
        SensitivePassword? backupPassword = null;
        try
        {
            if (options.Command == BootstrapCommand.Ensure)
            {
                foreach (var (purpose, path) in options.PasswordFiles)
                    result.Add(purpose, SensitivePassword.Read(path));
                backupPassword = SensitivePassword.Read(
                    options.BackupPasswordFile ?? throw InvalidState());
            }
            else if (options.Command is BootstrapCommand.Rotate or BootstrapCommand.ResetPassword)
            {
                var password = SensitivePassword.Read(
                    options.RotatePasswordFile ?? throw InvalidState());
                if (options.RotateBackup)
                    backupPassword = password;
                else
                    result.Add(options.RotatePurpose ?? throw InvalidState(), password);
            }
            return new LoadedSecrets(result, backupPassword);
        }
        catch
        {
            foreach (var secret in result.Values) secret.Dispose();
            backupPassword?.Dispose();
            throw;
        }
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

    private async Task<EnsureResult> EnsureAsync(
        NpgsqlConnection connection,
        string adminRole,
        IReadOnlyDictionary<LoginPurpose, SensitivePassword> passwords,
        SensitivePassword backupPassword,
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
            allowKnownRotations: true, allowBackupRoles: backupPhaseReady,
            allowBackupValidityExtension: backupPhaseReady);
        var ownerAlreadyExists = await ReadRoleAsync(
            connection, transaction, options.Contract.Owner.Name, cancellationToken) is not null;
        await EnsureExtensionsAsync(connection, transaction, adminRole, cancellationToken);

        foreach (var role in options.Contract.StableRoles)
            await EnsureRoleAsync(connection, transaction, role, passwordVerifier: null, cancellationToken);
        var currentLogins = new Dictionary<ManagedRole, SensitivePassword>();
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            var versions = await ReadManagedLoginRolesAsync(
                connection, transaction, purpose, cancellationToken);
            var current = versions.Count == 0
                ? options.Contract.Login(purpose, 1)
                : versions.MaxBy(role => role.LoginVersion) ?? throw InvalidState();
            await EnsureRoleAsync(connection, transaction, current,
                passwords[purpose].CreateVerifier(), cancellationToken);
            currentLogins.Add(current, passwords[purpose]);
        }
        if (backupPhaseReady)
        {
            if (await ReadRoleAsync(connection, transaction, backup.Name, cancellationToken) is null)
                await ValidateNewBackupValidityAsync(
                    connection, transaction, backup.ValidUntilUtc!.Value,
                    requireV1Overlap: false, cancellationToken);
            await EnsureRoleAsync(connection, transaction, backup,
                backupPassword.CreateVerifier(), cancellationToken,
                allowBackupValidityExtension: true);
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
        return new EnsureResult(backupPhaseReady, currentLogins);
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
        string passwordVerifier,
        CancellationToken cancellationToken)
    {
        var login = options.Contract.Login(purpose, version);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetLocalTimeoutsAsync(connection, transaction, cancellationToken);
        var existingVersions = await ReadManagedLoginRolesAsync(
            connection, transaction, purpose, cancellationToken);
        var currentVersion = existingVersions.Max(role => role.LoginVersion) ?? throw InvalidState();
        var isIdempotentRetry = version > 1 && currentVersion == version &&
                                existingVersions.Any(role => role.LoginVersion == version);
        if (!isIdempotentRetry && version != currentVersion + 1)
            throw TopologyRejected("login_version_not_next");
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
        await EnsureRoleAsync(connection, transaction, login, passwordVerifier, cancellationToken);
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
        string passwordVerifier,
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
        await EnsureRoleAsync(connection, transaction, login, passwordVerifier, cancellationToken);
        await VerifyContractAsync(connection, transaction, adminRole, requireBackup: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return login;
    }

    private async Task<ManagedRole> ResetPasswordAsync(
        NpgsqlConnection connection,
        string adminRole,
        LoginPurpose purpose,
        int version,
        string passwordVerifier,
        CancellationToken cancellationToken)
    {
        var login = options.Contract.Login(purpose, version);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetLocalTimeoutsAsync(connection, transaction, cancellationToken);
        var backupPhaseReady = await IsBackupPhaseReadyAsync(
            connection, transaction, cancellationToken);
        await VerifyContractAsync(
            connection, transaction, adminRole, backupPhaseReady, cancellationToken);
        var versions = await ReadManagedLoginRolesAsync(
            connection, transaction, purpose, cancellationToken);
        if (versions.Max(role => role.LoginVersion) != version ||
            versions.All(role => role.LoginVersion != version))
            throw TopologyRejected("reset_target_not_current");
        await AlterRolePasswordAsync(
            connection, transaction, login, passwordVerifier, cancellationToken);
        await VerifyContractAsync(
            connection, transaction, adminRole, backupPhaseReady, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return login;
    }

    private async Task RetireAsync(
        NpgsqlConnection connection,
        string adminRole,
        LoginPurpose purpose,
        int retiredVersion,
        int replacementVersion,
        TimeSpan drainTimeout,
        CancellationToken cancellationToken)
    {
        var retired = options.Contract.Login(purpose, retiredVersion);
        var replacement = options.Contract.Login(purpose, replacementVersion);
        if (retiredVersion >= replacementVersion)
            throw TopologyRejected("retire_version_order_invalid");

        await using (var stage = await connection.BeginTransactionAsync(cancellationToken))
        {
            await SetLocalTimeoutsAsync(connection, stage, cancellationToken);
            var backupPhaseReady = await IsBackupPhaseReadyAsync(connection, stage, cancellationToken);
            var versions = await ReadManagedLoginRolesAsync(
                connection, stage, purpose, cancellationToken);
            if (versions.Max(role => role.LoginVersion) != replacementVersion ||
                versions.All(role => role.LoginVersion != retiredVersion) ||
                versions.All(role => role.LoginVersion != replacementVersion))
                throw TopologyRejected("retire_version_set_invalid");
            var retiredState = await ReadRoleAsync(
                connection, stage, retired.Name, cancellationToken) ??
                throw TopologyRejected("managed_role_missing");
            await VerifyContractAsync(
                connection, stage, adminRole, backupPhaseReady, cancellationToken,
                allowedNoLoginRole: retiredState.CanLogin ? null : retired.Name);
            if (retiredState.CanLogin)
            {
                var sql = await FormatSqlAsync(connection, stage,
                    "SELECT pg_catalog.format('ALTER ROLE %I NOLOGIN', $1)",
                    cancellationToken, retired.Name);
                await ExecuteSqlAsync(connection, stage, sql, cancellationToken);
            }
            await stage.CommitAsync(cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        while (await HasActiveSessionsAsync(connection, retired.Name, cancellationToken))
        {
            if (stopwatch.Elapsed >= drainTimeout)
                throw TopologyRejected("retired_login_sessions_active");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        await using var finalize = await connection.BeginTransactionAsync(cancellationToken);
        await SetLocalTimeoutsAsync(connection, finalize, cancellationToken);
        var finalBackupPhaseReady = await IsBackupPhaseReadyAsync(
            connection, finalize, cancellationToken);
        await VerifyContractAsync(
            connection, finalize, adminRole, finalBackupPhaseReady, cancellationToken,
            allowedNoLoginRole: retired.Name);
        var finalRetired = await ReadRoleAsync(
            connection, finalize, retired.Name, cancellationToken) ??
            throw TopologyRejected("managed_role_missing");
        if (finalRetired.CanLogin ||
            await HasActiveSessionsAsync(connection, retired.Name, cancellationToken, finalize))
            throw TopologyRejected("retired_login_sessions_active");
        await RevokeLoginMembershipsAsync(
            connection, finalize, purpose, retired, cancellationToken);
        var drop = await FormatSqlAsync(connection, finalize,
            "SELECT pg_catalog.format('DROP ROLE %I', $1)", cancellationToken, retired.Name);
        await ExecuteSqlAsync(connection, finalize, drop, cancellationToken);
        await VerifyContractAsync(
            connection, finalize, adminRole, finalBackupPhaseReady, cancellationToken);
        await finalize.CommitAsync(cancellationToken);
    }

    private async Task<bool> HasActiveSessionsAsync(
        NpgsqlConnection connection,
        string role,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)>0 FROM pg_catalog.pg_stat_activity
             WHERE usename=$1 AND pid<>pg_catalog.pg_backend_pid()
            """, connection, transaction);
        command.Parameters.AddWithValue(role);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task RevokeLoginMembershipsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LoginPurpose purpose,
        ManagedRole login,
        CancellationToken cancellationToken)
    {
        await ExecuteSqlAsync(connection, transaction,
            $"REVOKE {QuoteIdentifier(options.Contract.Capability(purpose).Name)} " +
            $"FROM {QuoteIdentifier(login.Name)}", cancellationToken);
        if (purpose == LoginPurpose.Migrator)
            await ExecuteSqlAsync(connection, transaction,
                $"REVOKE {QuoteIdentifier(options.Contract.Owner.Name)} " +
                $"FROM {QuoteIdentifier(login.Name)}", cancellationToken);
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
        bool allowBackupRoles = true,
        bool allowBackupValidityExtension = false)
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
                    if (!string.Equals(marker, role.Marker, StringComparison.Ordinal) &&
                        (!allowBackupValidityExtension ||
                         !TryResolveSameManagedBackupRole(role, marker, out _)))
                        throw RoleCollision();
                    continue;
                }

                if (allowKnownRotations && marker is not null &&
                    options.Contract.TryResolveManagedMarker(marker, out var resolved) &&
                    resolved.Kind == ManagedRoleKind.Login &&
                    resolved.LoginVersion is { } resolvedVersion &&
                    RoleContract.IsAllowedLoginVersion(resolvedVersion) &&
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
                    role.Marker, StringComparison.Ordinal) &&
                (!allowBackupValidityExtension ||
                 !TryResolveSameManagedBackupRole(
                     role, Convert.ToString(marker, CultureInfo.InvariantCulture), out _)))
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

    internal static string DatabaseCode(string sqlState) => sqlState switch
    {
        "42501" => "admin_privilege_insufficient",
        "3D000" => "target_database_missing",
        _ when sqlState.Length == 5 &&
               sqlState.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'Z') =>
            $"database_operation_failed_sqlstate_{sqlState.ToLowerInvariant()}",
        _ => "database_operation_failed_sqlstate_invalid",
    };

    private static string FormatLoginVersions(IEnumerable<ManagedRole> logins) =>
        string.Join(',', logins
            .OrderBy(role => role.Purpose, StringComparer.Ordinal)
            .Select(role => $"{role.Purpose}:v{role.LoginVersion}"));

    private sealed record EnsureResult(
        bool BackupManaged,
        IReadOnlyDictionary<ManagedRole, SensitivePassword> CurrentLogins);
}
