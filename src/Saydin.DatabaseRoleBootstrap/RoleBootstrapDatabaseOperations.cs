using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Npgsql.Replication;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap;

internal sealed partial class RoleBootstrapRunner
{
    private sealed record ExistingRole(
        string Name,
        bool CanLogin,
        bool Superuser,
        bool CreateRole,
        bool CreateDatabase,
        bool Inherit,
        bool Replication,
        bool BypassRls,
        int ConnectionLimit,
        string? ValidUntilUtc,
        bool ConfigIsNull,
        string? Password,
        string? Marker);

    private sealed record Membership(
        string GrantedRole,
        string Member,
        string Grantor,
        bool Admin,
        bool Inherit,
        bool Set);

    private sealed record ExpectedMembership(
        string GrantedRole,
        string Member,
        string Grantor,
        bool Admin,
        bool Inherit,
        bool Set);

    private sealed record AclEntry(string Grantee, string Grantor, string Privilege, bool Grantable);

    private async Task EnsureExtensionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        await EnsureExtensionAsync(connection, transaction, "timescaledb",
            options.TimescaleVersion, adminRole, cancellationToken);
        await EnsureExtensionAsync(connection, transaction, "uuid-ossp",
            options.UuidOsspVersion, adminRole, cancellationToken);
    }

    private async Task EnsureExtensionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string extension,
        string expectedVersion,
        string adminRole,
        CancellationToken cancellationToken)
    {
        await using (var inspect = new NpgsqlCommand("""
            SELECT extension.extversion,
                   pg_catalog.pg_get_userbyid(extension.extowner),
                   namespace.nspname
              FROM pg_catalog.pg_extension extension
              JOIN pg_catalog.pg_namespace namespace ON namespace.oid=extension.extnamespace
             WHERE extension.extname=$1
            """, connection, transaction))
        {
            inspect.Parameters.AddWithValue(extension);
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var valid = string.Equals(reader.GetString(0), expectedVersion, StringComparison.Ordinal) &&
                            string.Equals(reader.GetString(1), adminRole, StringComparison.Ordinal) &&
                            string.Equals(reader.GetString(2), "public", StringComparison.Ordinal) &&
                            !await reader.ReadAsync(cancellationToken);
                if (!valid)
                    throw TopologyRejected("extension_contract_mismatch");
                return;
            }
        }

        await using (var available = new NpgsqlCommand("""
            SELECT count(*)=1
              FROM pg_catalog.pg_available_extension_versions
             WHERE name=$1 AND version=$2 AND installed=false
            """, connection, transaction))
        {
            available.Parameters.AddWithValue(extension);
            available.Parameters.AddWithValue(expectedVersion);
            if (await available.ExecuteScalarAsync(cancellationToken) is not true)
                throw TopologyRejected("extension_expected_version_unavailable");
        }

        var quotedExtension = QuoteIdentifier(extension);
        var quotedVersion = QuoteLiteral(expectedVersion);
        await using var create = new NpgsqlCommand(
            $"CREATE EXTENSION {quotedExtension} WITH SCHEMA public VERSION {quotedVersion}",
            connection, transaction)
        {
            CommandTimeout = Seconds(options.Timeouts.Statement),
        };
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task EnsureRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ManagedRole role,
        string? passwordVerifier,
        CancellationToken cancellationToken,
        bool allowBackupValidityExtension = false)
    {
        var existing = await ReadRoleAsync(connection, transaction, role.Name, cancellationToken);
        var isPasswordlessScheduler =
            string.Equals(role.Name, options.Contract.TimescaleScheduler.Name, StringComparison.Ordinal);
        if (role.Kind == ManagedRoleKind.Login && !isPasswordlessScheduler &&
            !PostgresScramSha256Verifier.IsCanonical(passwordVerifier))
            throw TopologyRejected("password_verifier_invalid");
        if (existing is null)
        {
            if (role.Kind == ManagedRoleKind.Login && passwordVerifier is null && !isPasswordlessScheduler)
                throw TopologyRejected("login_password_missing");
            var createSql = isPasswordlessScheduler
                ? $"CREATE ROLE {QuoteIdentifier(role.Name)} LOGIN NOSUPERUSER NOCREATEDB " +
                  "NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 0 PASSWORD NULL"
                : role.Purpose == "backup"
                ? await FormatSqlAsync(connection, transaction,
                    "SELECT pg_catalog.format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT REPLICATION NOBYPASSRLS CONNECTION LIMIT 2 VALID UNTIL %L PASSWORD %L', $1, $2, $3)",
                    cancellationToken, role.Name,
                    RoleContract.FormatBackupValidUntil(role.ValidUntilUtc!.Value), passwordVerifier!)
                : role.Kind == ManagedRoleKind.Login
                ? await FormatSqlAsync(connection, transaction,
                    "SELECT pg_catalog.format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1 PASSWORD %L', $1, $2)",
                    cancellationToken, role.Name, passwordVerifier!)
                : $"CREATE ROLE {QuoteIdentifier(role.Name)} NOLOGIN NOSUPERUSER NOCREATEDB " +
                  "NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1";
            await ExecuteSqlAsync(connection, transaction, createSql, cancellationToken);
            await SetRoleMarkerAsync(connection, transaction, role, cancellationToken);
        }
        else if (!string.Equals(existing.Marker, role.Marker, StringComparison.Ordinal))
        {
            if (!allowBackupValidityExtension ||
                !TryResolveSameManagedBackupRole(role, existing.Marker, out var currentBackup))
                throw RoleCollision();

            await ExtendManagedBackupValidityAsync(
                connection, transaction, existing, currentBackup, role, cancellationToken);
        }

        var updated = await ReadRoleAsync(connection, transaction, role.Name, cancellationToken) ??
                      throw TopologyRejected("managed_role_missing_after_ensure");
        VerifyRoleAttributes(updated, role);
    }

    private async Task AlterRolePasswordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ManagedRole role,
        string passwordVerifier,
        CancellationToken cancellationToken)
    {
        if (role.Kind != ManagedRoleKind.Login || role.Purpose == "timescale_scheduler" ||
            !PostgresScramSha256Verifier.IsCanonical(passwordVerifier))
            throw TopologyRejected("password_verifier_invalid");
        var existing = await ReadRoleAsync(
            connection, transaction, role.Name, cancellationToken) ??
            throw TopologyRejected("managed_role_missing");
        if (!string.Equals(existing.Marker, role.Marker, StringComparison.Ordinal))
            throw RoleCollision();
        VerifyRoleAttributes(existing, role);
        var sql = await FormatSqlAsync(connection, transaction,
            "SELECT pg_catalog.format('ALTER ROLE %I PASSWORD %L', $1, $2)",
            cancellationToken, role.Name, passwordVerifier);
        await ExecuteSqlAsync(connection, transaction, sql, cancellationToken);
        var updated = await ReadRoleAsync(
            connection, transaction, role.Name, cancellationToken) ??
            throw TopologyRejected("managed_role_missing_after_password_reset");
        VerifyRoleAttributes(updated, role);
        if (!CryptographicEquals(updated.Password ?? string.Empty, passwordVerifier))
            throw TopologyRejected("password_verifier_postcondition_failed");
    }

    private bool TryResolveSameManagedBackupRole(
        ManagedRole expected,
        string? marker,
        out ManagedRole current)
    {
        current = null!;
        if (expected.Purpose != "backup" || marker is null ||
            !options.Contract.TryResolveManagedMarker(marker, out var resolved) ||
            resolved.Purpose != "backup" || resolved.Kind != ManagedRoleKind.Login ||
            !string.Equals(resolved.Name, expected.Name, StringComparison.Ordinal) ||
            resolved.LoginVersion != expected.LoginVersion)
            return false;
        current = resolved;
        return true;
    }

    private async Task ExtendManagedBackupValidityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExistingRole existing,
        ManagedRole current,
        ManagedRole expected,
        CancellationToken cancellationToken)
    {
        if (current.ValidUntilUtc is null || expected.ValidUntilUtc is null ||
            existing.ValidUntilUtc is null ||
            !string.Equals(existing.ValidUntilUtc,
                RoleContract.FormatBackupValidUntil(current.ValidUntilUtc.Value),
                StringComparison.Ordinal))
            throw TopologyRejected("managed_role_attribute_mismatch");
        if (expected.ValidUntilUtc.Value <= current.ValidUntilUtc.Value)
            throw TopologyRejected("backup_valid_until_regression");

        await ValidateNewBackupValidityAsync(
            connection, transaction, expected.ValidUntilUtc.Value,
            requireV1Overlap: false, cancellationToken);
        var sql = await FormatSqlAsync(connection, transaction,
            "SELECT pg_catalog.format('ALTER ROLE %I VALID UNTIL %L', $1, $2)",
            cancellationToken, expected.Name,
            RoleContract.FormatBackupValidUntil(expected.ValidUntilUtc.Value));
        await ExecuteSqlAsync(connection, transaction, sql, cancellationToken);
        await SetRoleMarkerAsync(connection, transaction, expected, cancellationToken);
    }

    private async Task SetRoleMarkerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ManagedRole role,
        CancellationToken cancellationToken)
    {
        var sql = await FormatSqlAsync(connection, transaction,
            "SELECT pg_catalog.format('COMMENT ON ROLE %I IS %L', $1, $2)",
            cancellationToken, role.Name, role.Marker);
        await ExecuteSqlAsync(connection, transaction, sql, cancellationToken);
    }

    private async Task EnsureMembershipsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            var logins = await ReadManagedLoginRolesAsync(
                connection, transaction, purpose, cancellationToken);
            if (logins.Count == 0)
                throw TopologyRejected("managed_login_current_missing");
            foreach (var login in logins)
                await EnsureMembershipForLoginAsync(
                    connection, transaction, purpose, login, cancellationToken);
        }

        var adminRole = await CurrentUserAsync(connection, transaction, cancellationToken);
        await EnsureMembershipAsync(connection, transaction,
            new ExpectedMembership(options.Contract.TimescaleScheduler.Name,
                options.Contract.Owner.Name, adminRole,
                Admin: false, Inherit: false, Set: true), cancellationToken);
        await EnsureMembershipAsync(connection, transaction,
            new ExpectedMembership("pg_monitor", options.Contract.ExporterCapability.Name,
                adminRole, Admin: false, Inherit: true, Set: false), cancellationToken);
        await RejectUnexpectedMembershipsAsync(connection, transaction, adminRole, cancellationToken);
    }

    private async Task EnsureMembershipForLoginAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LoginPurpose purpose,
        ManagedRole login,
        CancellationToken cancellationToken)
    {
        var adminRole = await CurrentUserAsync(connection, transaction, cancellationToken);
        await EnsureMembershipAsync(connection, transaction,
            new ExpectedMembership(options.Contract.Capability(purpose).Name, login.Name,
                adminRole, Admin: false, Inherit: true, Set: false), cancellationToken);
        if (purpose == LoginPurpose.Migrator)
        {
            await EnsureMembershipAsync(connection, transaction,
                new ExpectedMembership(options.Contract.Owner.Name, login.Name,
                    adminRole, Admin: false, Inherit: false, Set: true), cancellationToken);
        }
    }

    private async Task EnsureMembershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExpectedMembership expected,
        CancellationToken cancellationToken)
    {
        var rows = (await ReadMembershipsAsync(connection, transaction, cancellationToken))
            .Where(row => row.GrantedRole == expected.GrantedRole && row.Member == expected.Member)
            .ToList();
        if (rows.Count > 1)
            throw TopologyRejected("membership_grantor_ambiguous");
        if (rows.Count == 1)
        {
            var row = rows[0];
            if (row.Grantor != expected.Grantor ||
                row.Admin != expected.Admin || row.Inherit != expected.Inherit || row.Set != expected.Set)
                throw TopologyRejected("membership_contract_mismatch");
            return;
        }

        var sql = $"GRANT {QuoteIdentifier(expected.GrantedRole)} TO {QuoteIdentifier(expected.Member)} " +
                  $"WITH ADMIN {Bool(expected.Admin)}, INHERIT {Bool(expected.Inherit)}, SET {Bool(expected.Set)}";
        await ExecuteSqlAsync(connection, transaction, sql, cancellationToken);

        var actual = (await ReadMembershipsAsync(connection, transaction, cancellationToken))
            .SingleOrDefault(row => row.GrantedRole == expected.GrantedRole && row.Member == expected.Member);
        if (actual is null || actual.Grantor != expected.Grantor || actual.Admin != expected.Admin ||
            actual.Inherit != expected.Inherit || actual.Set != expected.Set)
            throw TopologyRejected("membership_postcondition_failed");
    }

    private async Task RejectUnexpectedMembershipsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        var managedRoles = await ReadManagedRolesAsync(connection, transaction, cancellationToken);
        var expected = ExpectedMemberships(managedRoles, adminRole).ToHashSet();
        var actual = await ReadMembershipsAsync(connection, transaction, cancellationToken);
        var relevant = actual.Where(row =>
                managedRoles.Contains(row.Member) || managedRoles.Contains(row.GrantedRole) ||
                row.GrantedRole == "pg_monitor" &&
                row.Member == options.Contract.ExporterCapability.Name)
            .Select(membership => new ExpectedMembership(
                membership.GrantedRole, membership.Member, membership.Grantor,
                membership.Admin, membership.Inherit, membership.Set))
            .ToHashSet();
        if (!relevant.SetEquals(expected))
            throw TopologyRejected("managed_role_membership_set_mismatch");
    }

    private IReadOnlyCollection<ExpectedMembership> ExpectedMemberships(
        IReadOnlySet<string> managedRoles,
        string adminRole)
    {
        var result = new List<ExpectedMembership>
        {
            new(options.Contract.TimescaleScheduler.Name, options.Contract.Owner.Name,
                adminRole, false, false, true),
            new("pg_monitor", options.Contract.ExporterCapability.Name, adminRole, false, true, false),
        };
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            foreach (var version in RoleContract.AllowedLoginVersions)
            {
                var login = options.Contract.Login(purpose, version);
                if (!managedRoles.Contains(login.Name)) continue;
                result.Add(new ExpectedMembership(
                    options.Contract.Capability(purpose).Name, login.Name, adminRole, false, true, false));
                if (purpose == LoginPurpose.Migrator)
                    result.Add(new ExpectedMembership(
                        options.Contract.Owner.Name, login.Name, adminRole, false, false, true));
            }
        }
        return result;
    }

    private async Task EnsureDatabaseControlPlaneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        bool ownerAlreadyExists,
        CancellationToken cancellationToken)
    {
        var database = QuoteIdentifier(options.Contract.Database);
        var currentOwner = await ReadDatabaseOwnerAsync(connection, transaction, cancellationToken);
        if (!ownerAlreadyExists)
        {
            if (!string.Equals(currentOwner, adminRole, StringComparison.Ordinal))
                throw TopologyRejected("database_owner_claim_conflict");
            await ExecuteSqlAsync(connection, transaction,
                $"ALTER DATABASE {database} OWNER TO {QuoteIdentifier(options.Contract.Owner.Name)}",
                cancellationToken);
            await ExecuteSqlAsync(connection, transaction,
                $"REVOKE CONNECT, TEMPORARY ON DATABASE {database} FROM PUBLIC", cancellationToken);
            await ExecuteSqlAsync(connection, transaction,
                "REVOKE CREATE, USAGE ON SCHEMA public FROM PUBLIC", cancellationToken);
        }
        else if (!string.Equals(currentOwner, options.Contract.Owner.Name, StringComparison.Ordinal))
        {
            throw TopologyRejected("database_owner_mismatch");
        }

        foreach (var capability in options.Contract.Capabilities)
        {
            if (!await HasPrivilegeAsync(connection, transaction,
                    "SELECT pg_catalog.has_database_privilege($1,$2,'CONNECT')",
                    capability.Name, options.Contract.Database, cancellationToken))
                await ExecuteSqlAsync(connection, transaction,
                    $"GRANT CONNECT ON DATABASE {database} TO {QuoteIdentifier(capability.Name)}",
                    cancellationToken);
        }
        if (!await HasPrivilegeAsync(connection, transaction,
                "SELECT pg_catalog.has_database_privilege($1,$2,'CONNECT')",
                options.Contract.TimescaleScheduler.Name, options.Contract.Database, cancellationToken))
            await ExecuteSqlAsync(connection, transaction,
                $"GRANT CONNECT ON DATABASE {database} TO " +
                QuoteIdentifier(options.Contract.TimescaleScheduler.Name), cancellationToken);

        foreach (var purpose in PublicSchemaUsers)
        {
            var capability = options.Contract.Capability(purpose).Name;
            if (!await HasPrivilegeAsync(connection, transaction,
                    "SELECT pg_catalog.has_schema_privilege($1,'public','USAGE')",
                    capability, null, cancellationToken))
                await ExecuteSqlAsync(connection, transaction,
                    $"GRANT USAGE ON SCHEMA public TO {QuoteIdentifier(capability)}",
                    cancellationToken);
        }
        if (!await HasPrivilegeAsync(connection, transaction,
                "SELECT pg_catalog.has_schema_privilege($1,'public','USAGE')",
                options.Contract.TimescaleScheduler.Name, null, cancellationToken))
            await ExecuteSqlAsync(connection, transaction,
                "GRANT USAGE ON SCHEMA public TO " +
                QuoteIdentifier(options.Contract.TimescaleScheduler.Name), cancellationToken);

        if (!ownerAlreadyExists)
        {
            await ExecuteSqlAsync(connection, transaction,
                "REVOKE EXECUTE ON FUNCTION pg_catalog.pg_control_system() FROM PUBLIC", cancellationToken);
        }
        foreach (var role in new[]
                 {
                     options.Contract.Owner,
                     options.Contract.MigratorCapability,
                     options.Contract.AuditCapability,
                 })
        {
            if (!await HasPrivilegeAsync(connection, transaction,
                    "SELECT pg_catalog.has_function_privilege($1,'pg_catalog.pg_control_system()','EXECUTE')",
                    role.Name, null, cancellationToken))
                await ExecuteSqlAsync(connection, transaction,
                    $"GRANT EXECUTE ON FUNCTION pg_catalog.pg_control_system() TO {QuoteIdentifier(role.Name)}",
                    cancellationToken);
        }
    }

    private async Task<string> ReadDatabaseOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.pg_get_userbyid(datdba)
              FROM pg_catalog.pg_database WHERE datname=$1
            """, connection, transaction);
        command.Parameters.AddWithValue(options.Contract.Database);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
                   CultureInfo.InvariantCulture) ??
               throw TopologyRejected("database_owner_unavailable");
    }

    private static async Task<bool> HasPrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string role,
        string? database,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(role);
        if (database is not null) command.Parameters.AddWithValue(database);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task VerifyContractAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        bool requireBackup,
        CancellationToken cancellationToken,
        string? allowedNoLoginRole = null)
    {
        await VerifyExtensionsAsync(connection, transaction, adminRole, cancellationToken);
        var managedRoles = await ReadManagedRolesAsync(connection, transaction, cancellationToken);
        var currentLoginVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            var candidates = await ReadManagedLoginRolesAsync(
                connection, transaction, purpose, cancellationToken);
            if (candidates.Count == 0)
                throw TopologyRejected("managed_login_current_missing");
            currentLoginVersions[RoleContract.PurposeName(purpose)] =
                candidates.Max(role => role.LoginVersion) ?? throw InvalidState();
        }
        var requiredRoles = requireBackup
            ? options.Contract.StableRoles.Append(
                options.Contract.BackupLogin(1, options.BackupV1ValidUntilUtc))
            : options.Contract.StableRoles;
        foreach (var expected in requiredRoles)
        {
            var actual = await ReadRoleAsync(connection, transaction, expected.Name, cancellationToken) ??
                         throw TopologyRejected("managed_role_missing");
            if (!string.Equals(actual.Marker, expected.Marker, StringComparison.Ordinal))
                throw RoleCollision();
            VerifyRoleAttributes(
                actual, expected,
                allowNoLogin: string.Equals(expected.Name, allowedNoLoginRole, StringComparison.Ordinal));
        }
        foreach (var name in managedRoles)
        {
            var role = await ReadRoleAsync(connection, transaction, name, cancellationToken) ??
                       throw TopologyRejected("managed_role_missing");
            if (role.Marker is null || !options.Contract.TryResolveManagedMarker(
                    role.Marker, out var expected))
                throw RoleCollision();
            if (!requireBackup && expected.Purpose == "backup")
                throw RoleCollision();
            if (!string.Equals(name, expected.Name, StringComparison.Ordinal) ||
                !options.Contract.IsExactMarker(expected, role.Marker))
                throw RoleCollision();
            VerifyRoleAttributes(
                role, expected,
                allowNoLogin:
                    string.Equals(expected.Name, allowedNoLoginRole, StringComparison.Ordinal)
                    || expected.LoginVersion is { } version
                    && currentLoginVersions.TryGetValue(expected.Purpose, out var currentVersion)
                    && version < currentVersion);
        }
        await RejectUnexpectedMembershipsAsync(connection, transaction, adminRole, cancellationToken);
        await VerifyDatabaseControlPlaneAsync(
            connection, transaction, managedRoles, adminRole, allowedNoLoginRole, cancellationToken);
        await VerifyTimescaleTransitionControlPlaneAsync(
            connection, transaction, adminRole, cancellationToken);
        await VerifyPrincipalRetentionTransitionControlPlaneAsync(
            connection, transaction, adminRole, cancellationToken);
        if (requireBackup)
            await VerifyBackupIsolationAndAvailabilityAsync(
                connection, transaction, managedRoles, cancellationToken);
    }

    private async Task<bool> IsBackupPhaseReadyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand("""
            SELECT pg_catalog.to_regclass('public.saydin_migration_control') IS NOT NULL,
                   pg_catalog.to_regclass('public.saydin_role_contract') IS NOT NULL,
                   pg_catalog.to_regclass('public.schema_migrations') IS NOT NULL
            """, connection, transaction))
        {
            await using var reader = await exists.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw TopologyRejected("migration_phase_unavailable");
            var controlExists = reader.GetBoolean(0);
            var contractExists = reader.GetBoolean(1);
            var migrationsExist = reader.GetBoolean(2);
            if (await reader.ReadAsync(cancellationToken))
                throw TopologyRejected("migration_phase_unavailable");
            if (!contractExists) return false;
            if (!controlExists || !migrationsExist)
                throw TopologyRejected("migration_phase_unavailable");
        }

        await using (var contract = new NpgsqlCommand("""
            SELECT (SELECT count(*)=1 AND bool_and(
                               contract_schema_version=1 AND contract_sha256=$1
                               AND deployment_id=$2 AND database_name=current_database()
                               AND system_identifier_sha256=$3 AND role_prefix=$4
                               AND owner_role=$5 AND timescale_scheduler_role=$6)
                      FROM public.saydin_role_contract)
               AND (SELECT count(*)=1 AND bool_and(state='ready')
                      FROM public.saydin_migration_control WHERE singleton=1)
               AND (SELECT count(*)=1 AND bool_and(state='succeeded')
                      FROM public.schema_migrations
                     WHERE version='019_privilege_separation')
            """, connection, transaction))
        {
            contract.Parameters.AddWithValue(options.ContractSha256);
            contract.Parameters.AddWithValue(options.Contract.DeploymentId);
            contract.Parameters.AddWithValue(options.Contract.SystemIdentifierSha256);
            contract.Parameters.AddWithValue(options.Contract.Prefix);
            contract.Parameters.AddWithValue(options.Contract.Owner.Name);
            contract.Parameters.AddWithValue(options.Contract.TimescaleScheduler.Name);
            if (await contract.ExecuteScalarAsync(cancellationToken) is not true)
                throw TopologyRejected("migration_phase_unavailable");
        }

        await using var retention = new NpgsqlCommand("""
            SELECT count(*)=1 AND bool_and(state='succeeded')
              FROM public.schema_migrations
             WHERE version='022_principal_retention'
            """, connection, transaction);
        return await retention.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task VerifyExtensionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string adminRole,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT extension.extname, extension.extversion,
                   pg_catalog.pg_get_userbyid(extension.extowner), namespace.nspname
              FROM pg_catalog.pg_extension extension
              JOIN pg_catalog.pg_namespace namespace ON namespace.oid=extension.extnamespace
             WHERE extension.extname IN ('timescaledb','uuid-ossp')
             ORDER BY extension.extname COLLATE "C"
            """, connection, transaction);
        var rows = new Dictionary<string, (string Version, string Owner, string Schema)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        if (rows.Count != 2 || !rows.TryGetValue("timescaledb", out var timescale) ||
            !rows.TryGetValue("uuid-ossp", out var uuid) ||
            timescale != (options.TimescaleVersion, adminRole, "public") ||
            uuid != (options.UuidOsspVersion, adminRole, "public"))
            throw TopologyRejected("extension_contract_mismatch");
    }

    private async Task VerifyDatabaseControlPlaneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlySet<string> managedRoles,
        string adminRole,
        string? allowedNoLoginRole,
        CancellationToken cancellationToken)
    {
        await using (var owner = new NpgsqlCommand("""
            SELECT pg_catalog.pg_get_userbyid(datdba)
              FROM pg_catalog.pg_database WHERE datname=$1
            """, connection, transaction))
        {
            owner.Parameters.AddWithValue(options.Contract.Database);
            if (!string.Equals(Convert.ToString(await owner.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture), options.Contract.Owner.Name, StringComparison.Ordinal))
                throw TopologyRejected("database_owner_mismatch");
        }

        var expectedDatabaseAcl = new HashSet<AclEntry>
        {
            new(options.Contract.Owner.Name, options.Contract.Owner.Name, "CONNECT", false),
            new(options.Contract.Owner.Name, options.Contract.Owner.Name, "CREATE", false),
            new(options.Contract.Owner.Name, options.Contract.Owner.Name, "TEMPORARY", false),
        };
        foreach (var capability in options.Contract.Capabilities)
            expectedDatabaseAcl.Add(new AclEntry(
                capability.Name, options.Contract.Owner.Name, "CONNECT", false));
        expectedDatabaseAcl.Add(new AclEntry(options.Contract.TimescaleScheduler.Name,
            options.Contract.Owner.Name, "CONNECT", false));
        var databaseAcl = await ReadAclAsync(connection, transaction, """
            SELECT coalesce(role.rolname, CASE WHEN acl.grantee=0 THEN 'PUBLIC' END),
                   grantor.rolname, acl.privilege_type, acl.is_grantable
              FROM pg_catalog.pg_database database,
                   LATERAL pg_catalog.aclexplode(
                       coalesce(database.datacl, pg_catalog.acldefault('d', database.datdba))) acl
              LEFT JOIN pg_catalog.pg_roles role ON role.oid=acl.grantee
              LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
             WHERE database.datname=$1
            """, options.Contract.Database, cancellationToken);
        if (!AclSetEquals(databaseAcl, expectedDatabaseAcl))
            throw TopologyRejected("database_acl_set_mismatch");

        var expectedSchemaAcl = new HashSet<AclEntry>
        {
            new("pg_database_owner", "pg_database_owner", "CREATE", false),
            new("pg_database_owner", "pg_database_owner", "USAGE", false),
            new(options.Contract.ApiCapability.Name, "pg_database_owner", "USAGE", false),
            new(options.Contract.IngestionCapability.Name, "pg_database_owner", "USAGE", false),
            new(options.Contract.CalendarImporterCapability.Name, "pg_database_owner", "USAGE", false),
            new(options.Contract.AuditCapability.Name, "pg_database_owner", "USAGE", false),
            new(options.Contract.TimescaleScheduler.Name, "pg_database_owner", "USAGE", false),
        };
        var schemaAcl = await ReadAclAsync(connection, transaction, """
            SELECT coalesce(role.rolname, CASE WHEN acl.grantee=0 THEN 'PUBLIC' END),
                   grantor.rolname, acl.privilege_type, acl.is_grantable
              FROM pg_catalog.pg_namespace namespace,
                   LATERAL pg_catalog.aclexplode(
                       coalesce(namespace.nspacl, pg_catalog.acldefault('n', namespace.nspowner))) acl
              LEFT JOIN pg_catalog.pg_roles role ON role.oid=acl.grantee
              LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
             WHERE namespace.nspname='public'
            """, null, cancellationToken);
        if (!AclSetEquals(schemaAcl, expectedSchemaAcl))
            throw TopologyRejected("schema_acl_set_mismatch");

        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            var capability = options.Contract.Capability(purpose).Name;
            var needsSchema = PublicSchemaUsers.Contains(purpose);
            await using var command = new NpgsqlCommand("""
                SELECT pg_catalog.has_database_privilege($1,$2,'CONNECT'),
                       pg_catalog.has_database_privilege($1,$2,'TEMP'),
                       pg_catalog.has_database_privilege($1,$2,'CREATE'),
                       pg_catalog.has_schema_privilege($1,'public','USAGE'),
                       pg_catalog.has_schema_privilege($1,'public','CREATE')
                """, connection, transaction);
            command.Parameters.AddWithValue(capability);
            command.Parameters.AddWithValue(options.Contract.Database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0) || reader.GetBoolean(1) ||
                reader.GetBoolean(2) || reader.GetBoolean(3) != needsSchema || reader.GetBoolean(4) ||
                await reader.ReadAsync(cancellationToken))
                throw TopologyRejected("capability_database_acl_mismatch");
        }
        await using (var scheduler = new NpgsqlCommand("""
            SELECT pg_catalog.has_database_privilege($1,$2,'CONNECT'),
                   pg_catalog.has_database_privilege($1,$2,'TEMP'),
                   pg_catalog.has_database_privilege($1,$2,'CREATE'),
                   pg_catalog.has_schema_privilege($1,'public','USAGE'),
                   pg_catalog.has_schema_privilege($1,'public','CREATE'),
                   pg_catalog.has_function_privilege($1,
                       'pg_catalog.pg_control_system()','EXECUTE')
            """, connection, transaction))
        {
            scheduler.Parameters.AddWithValue(options.Contract.TimescaleScheduler.Name);
            scheduler.Parameters.AddWithValue(options.Contract.Database);
            await using var reader = await scheduler.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0) || reader.GetBoolean(1) ||
                reader.GetBoolean(2) || !reader.GetBoolean(3) || reader.GetBoolean(4) || reader.GetBoolean(5) ||
                await reader.ReadAsync(cancellationToken))
                throw TopologyRejected("scheduler_database_acl_mismatch");
        }

        var allowedPgControl = new HashSet<string>(StringComparer.Ordinal)
        {
            options.Contract.Owner.Name,
            options.Contract.MigratorCapability.Name,
            options.Contract.AuditCapability.Name,
        };
        var observed = new List<AclEntry>();
        {
            await using var pgControl = new NpgsqlCommand("""
                SELECT coalesce(role.rolname, CASE WHEN acl.grantee=0 THEN 'PUBLIC' END),
                       grantor.rolname, acl.privilege_type, acl.is_grantable,
                       owner.rolname, namespace.nspname, function.proowner
                  FROM pg_catalog.pg_proc function
                  JOIN pg_catalog.pg_namespace namespace ON namespace.oid=function.pronamespace
                  JOIN pg_catalog.pg_roles owner ON owner.oid=function.proowner
                  CROSS JOIN LATERAL pg_catalog.aclexplode(
                      coalesce(function.proacl, pg_catalog.acldefault('f', function.proowner))) acl
                  LEFT JOIN pg_catalog.pg_roles role ON role.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE function.oid='pg_catalog.pg_control_system()'::pg_catalog.regprocedure
                """, connection, transaction);
            await using var aclReader = await pgControl.ExecuteReaderAsync(cancellationToken);
            while (await aclReader.ReadAsync(cancellationToken))
            {
                if (aclReader.IsDBNull(0) || aclReader.IsDBNull(1) ||
                    aclReader.GetString(4) != adminRole || aclReader.GetString(5) != "pg_catalog" ||
                    aclReader.GetFieldValue<uint>(6) != 10)
                    throw TopologyRejected("pg_control_acl_unexpected");
                observed.Add(new AclEntry(
                    aclReader.GetString(0), aclReader.GetString(1),
                    aclReader.GetString(2), aclReader.GetBoolean(3)));
            }
        }
        var expectedPgControl = new[]
            {
                new AclEntry(adminRole, adminRole, "EXECUTE", false),
                new AclEntry(options.Contract.Owner.Name, adminRole, "EXECUTE", false),
                new AclEntry(options.Contract.MigratorCapability.Name, adminRole, "EXECUTE", false),
                new AclEntry(options.Contract.AuditCapability.Name, adminRole, "EXECUTE", false),
            };
        if (!AclSetEquals(observed, expectedPgControl))
            throw TopologyRejected("pg_control_acl_mismatch");

        foreach (var name in managedRoles)
        {
            var expected = allowedPgControl.Contains(name);
            await using var direct = new NpgsqlCommand("""
                SELECT pg_catalog.has_function_privilege($1,
                    'pg_catalog.pg_control_system()','EXECUTE')
                """, connection, transaction);
            direct.Parameters.AddWithValue(name);
            var effective = await direct.ExecuteScalarAsync(cancellationToken) is true;
            var parsed = await ReadRoleAsync(connection, transaction, name, cancellationToken);
            // A login being retired is already NOLOGIN here, but it keeps its capability
            // membership until RevokeLoginMembershipsAsync runs after this verification.
            // Judging it by CanLogin alone would expect no pg_control access while the
            // inherited grant is still live, which fails closed on every migrator/audit
            // retirement. Judge the retiring role by its managed purpose instead.
            var retiring = string.Equals(name, allowedNoLoginRole, StringComparison.Ordinal);
            if (parsed?.CanLogin == true || (retiring && parsed is not null))
            {
                if (parsed.Marker is null || !options.Contract.TryResolveManagedMarker(
                        parsed.Marker, out var resolved))
                    throw RoleCollision();
                expected = resolved.Purpose is "migrator" or "audit";
            }
            if (effective != expected)
                throw TopologyRejected("pg_control_effective_acl_mismatch");
        }
    }

    private async Task VerifyBackupIsolationAndAvailabilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlySet<string> managedRoles,
        CancellationToken cancellationToken)
    {
        var backups = new List<ManagedRole>();
        foreach (var name in managedRoles)
        {
            var actual = await ReadRoleAsync(connection, transaction, name, cancellationToken);
            if (actual?.Marker is null ||
                !options.Contract.TryResolveManagedMarker(actual.Marker, out var resolved) ||
                resolved.Purpose != "backup")
                continue;
            backups.Add(resolved);
        }
        if (backups.Count is < 1 or > 2 || backups.All(role => role.LoginVersion != 1) ||
            backups.Select(role => role.LoginVersion).Distinct().Count() != backups.Count)
            throw TopologyRejected("backup_role_version_set_mismatch");

        var now = await ReadDatabaseClockAsync(connection, transaction, cancellationToken);
        if (backups.All(role => role.ValidUntilUtc!.Value < now.AddHours(24)))
            throw TopologyRejected("backup_role_rotation_horizon_insufficient");

        foreach (var backup in backups)
        {
            await using (var effective = new NpgsqlCommand("""
                SELECT pg_catalog.has_database_privilege($1,$2,'CONNECT'),
                       pg_catalog.has_database_privilege($1,$2,'CREATE'),
                       pg_catalog.has_database_privilege($1,$2,'TEMPORARY'),
                       pg_catalog.has_schema_privilege($1,'public','USAGE'),
                       pg_catalog.has_schema_privilege($1,'public','CREATE'),
                       pg_catalog.has_function_privilege(
                           $1,'pg_catalog.pg_control_system()','EXECUTE')
                """, connection, transaction))
            {
                effective.Parameters.AddWithValue(backup.Name);
                effective.Parameters.AddWithValue(options.Contract.Database);
                await using var reader = await effective.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) ||
                    Enumerable.Range(0, 6).Any(reader.GetBoolean) ||
                    await reader.ReadAsync(cancellationToken))
                    throw TopologyRejected("backup_effective_acl_mismatch");
            }

            await using var direct = new NpgsqlCommand("""
                WITH backup AS (
                    SELECT oid FROM pg_catalog.pg_roles WHERE rolname=$1
                ), direct_grant_or_ownership AS (
                    SELECT 1 FROM pg_catalog.pg_database object, backup
                     WHERE object.datdba=backup.oid
                        OR EXISTS (SELECT 1 FROM pg_catalog.aclexplode(object.datacl) acl
                            WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_namespace object, backup
                     WHERE object.nspowner=backup.oid
                        OR EXISTS (SELECT 1 FROM pg_catalog.aclexplode(object.nspacl) acl
                            WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_class object, backup
                     WHERE object.relowner=backup.oid
                        OR EXISTS (SELECT 1 FROM pg_catalog.aclexplode(object.relacl) acl
                            WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_attribute object
                      JOIN pg_catalog.pg_class relation ON relation.oid=object.attrelid
                      CROSS JOIN backup
                     WHERE object.attnum>0 AND NOT object.attisdropped
                       AND EXISTS (SELECT 1 FROM pg_catalog.aclexplode(object.attacl) acl
                           WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_proc object, backup
                     WHERE object.proowner=backup.oid
                        OR EXISTS (SELECT 1 FROM pg_catalog.aclexplode(object.proacl) acl
                            WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_default_acl object, backup
                     WHERE object.defaclrole=backup.oid
                        OR EXISTS (SELECT 1 FROM pg_catalog.aclexplode(object.defaclacl) acl
                            WHERE acl.grantee=backup.oid)
                )
                SELECT NOT EXISTS (SELECT 1 FROM direct_grant_or_ownership)
                """, connection, transaction);
            direct.Parameters.AddWithValue(backup.Name);
            if (await direct.ExecuteScalarAsync(cancellationToken) is not true)
                throw TopologyRejected("backup_direct_acl_or_ownership_detected");
        }
    }

    private async Task ValidateNewBackupValidityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset validUntilUtc,
        bool requireV1Overlap,
        CancellationToken cancellationToken)
    {
        var now = await ReadDatabaseClockAsync(connection, transaction, cancellationToken);
        var normalized = new DateTimeOffset(
            now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        if (validUntilUtc < normalized.AddHours(24) || validUntilUtc > normalized.AddDays(93))
            throw TopologyRejected("backup_valid_until_out_of_bounds");
        if (requireV1Overlap && options.BackupV1ValidUntilUtc < normalized.AddHours(24))
            throw TopologyRejected("backup_v1_overlap_insufficient");
    }

    private static async Task<DateTimeOffset> ReadDatabaseClockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT extract(epoch FROM pg_catalog.clock_timestamp())::bigint",
            connection, transaction);
        var seconds = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static async Task<IReadOnlyList<AclEntry>> ReadAclAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string? database,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (database is not null) command.Parameters.AddWithValue(database);
        var result = new List<AclEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                throw TopologyRejected("acl_principal_unresolved");
            result.Add(new AclEntry(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }
        return result;
    }

    private static bool AclSetEquals(
        IReadOnlyCollection<AclEntry> actual,
        IReadOnlyCollection<AclEntry> expected) =>
        actual.Count == expected.Count && actual.ToHashSet().SetEquals(expected);

    private static void VerifyRoleAttributes(
        ExistingRole actual,
        ManagedRole expected,
        bool allowNoLogin = false)
    {
        var shouldLogin = expected.Kind == ManagedRoleKind.Login;
        var shouldBePasswordless = expected.Purpose == "timescale_scheduler";
        var expectedValidUntil = expected.ValidUntilUtc is null
            ? null
            : RoleContract.FormatBackupValidUntil(expected.ValidUntilUtc.Value);
        if ((actual.CanLogin != shouldLogin &&
             !(allowNoLogin && shouldLogin && !actual.CanLogin)) ||
            actual.Superuser || actual.CreateRole || actual.CreateDatabase ||
            actual.Inherit || actual.Replication != expected.Replication || actual.BypassRls ||
            actual.ConnectionLimit != expected.ConnectionLimit ||
            !string.Equals(actual.ValidUntilUtc, expectedValidUntil, StringComparison.Ordinal) ||
            !actual.ConfigIsNull ||
            (shouldLogin && !shouldBePasswordless &&
             !PostgresScramSha256Verifier.IsCanonical(actual.Password)) ||
            ((!shouldLogin || shouldBePasswordless) && actual.Password is not null))
            throw TopologyRejected("managed_role_attribute_mismatch");
    }

    private static async Task<ExistingRole?> ReadRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT role.rolname, role.rolcanlogin, role.rolsuper, role.rolcreaterole,
                   role.rolcreatedb, role.rolinherit, role.rolreplication, role.rolbypassrls,
                   role.rolconnlimit,
                   CASE WHEN role.rolvaliduntil IS NULL THEN NULL ELSE
                       pg_catalog.to_char(role.rolvaliduntil AT TIME ZONE 'UTC',
                           'YYYY-MM-DD"T"HH24:MI:SS"Z"') END,
                   auth.rolpassword,
                   role.rolconfig IS NULL,
                   pg_catalog.shobj_description(role.oid, 'pg_authid')
              FROM pg_catalog.pg_roles role
              JOIN pg_catalog.pg_authid auth ON auth.oid=role.oid
             WHERE role.rolname=$1
            """, connection, transaction);
        command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = new ExistingRole(
            reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3),
            reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7),
            reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetBoolean(11),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(12) ? null : reader.GetString(12));
        if (await reader.ReadAsync(cancellationToken))
            throw TopologyRejected("managed_role_ambiguous");
        return result;
    }

    private async Task<IReadOnlyList<ManagedRole>> ReadManagedLoginRolesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LoginPurpose purpose,
        CancellationToken cancellationToken)
    {
        var result = new List<ManagedRole>();
        await using var command = new NpgsqlCommand("""
            SELECT role.rolname,pg_catalog.shobj_description(role.oid,'pg_authid')
              FROM pg_catalog.pg_roles role
             WHERE pg_catalog.left(role.rolname,pg_catalog.length($1)+1)=$1||'_'
             ORDER BY role.rolname COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue(options.Contract.Prefix);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(1) ||
                !options.Contract.TryResolveManagedMarker(reader.GetString(1), out var role) ||
                role.Kind != ManagedRoleKind.Login || role.Purpose == "backup" ||
                role.Purpose == "timescale_scheduler" ||
                !string.Equals(role.Name, reader.GetString(0), StringComparison.Ordinal))
                continue;
            if (ParsePurpose(role.Purpose) == purpose)
                result.Add(role);
        }
        return result;
    }

    private async Task<HashSet<string>> ReadManagedRolesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT role.rolname, pg_catalog.shobj_description(role.oid, 'pg_authid')
              FROM pg_catalog.pg_roles role
             WHERE pg_catalog.left(role.rolname, pg_catalog.length($1) + 1) = $1 || '_'
             ORDER BY role.rolname COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue(options.Contract.Prefix);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(1)) throw RoleCollision();
            roles.Add(reader.GetString(0));
        }
        return roles;
    }

    private async Task<List<Membership>> ReadMembershipsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT granted.rolname, member.rolname, grantor.rolname,
                   membership.admin_option, membership.inherit_option, membership.set_option
              FROM pg_catalog.pg_auth_members membership
              JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
              JOIN pg_catalog.pg_roles member ON member.oid=membership.member
              JOIN pg_catalog.pg_roles grantor ON grantor.oid=membership.grantor
             WHERE pg_catalog.left(granted.rolname, pg_catalog.length($1) + 1) = $1 || '_'
                OR pg_catalog.left(member.rolname, pg_catalog.length($1) + 1) = $1 || '_'
                OR (granted.rolname='pg_monitor' AND member.rolname=$2)
             ORDER BY granted.rolname COLLATE "C", member.rolname COLLATE "C", grantor.rolname COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue(options.Contract.Prefix);
        command.Parameters.AddWithValue(options.Contract.ExporterCapability.Name);
        var rows = new List<Membership>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new Membership(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        return rows;
    }

    private static async Task<string> CurrentUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_user", connection, transaction);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
                   CultureInfo.InvariantCulture) ??
               throw TopologyRejected("admin_identity_unavailable");
    }

    private async Task AuthenticateAsync(
        NpgsqlConnectionStringBuilder adminBuilder,
        IReadOnlyDictionary<ManagedRole, SensitivePassword> logins,
        VerifiedAdminTarget adminTarget,
        CancellationToken cancellationToken)
    {
        foreach (var (login, password) in logins)
        {
            var authenticationPassword = password.RevealForAuthentication();
            var builder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Username = login.Name,
                Password = authenticationPassword,
                ApplicationName = "saydin-role-bootstrap-auth-probe",
                Pooling = false,
                IncludeErrorDetail = false,
                Timeout = Seconds(options.Timeouts.Connect),
                CommandTimeout = Seconds(options.Timeouts.Statement),
            };
            try
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("""
                    SELECT current_database(),current_user,
                           pg_catalog.has_function_privilege(
                               current_user,'pg_catalog.pg_control_system()','EXECUTE'),
                           coalesce(pg_catalog.inet_server_addr()::text,'local'),
                           coalesce(pg_catalog.inet_server_port(),0)
                """, connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new BootstrapRejectedException(
                        "login_authentication_identity_mismatch", BootstrapExitCodes.AuthenticationRejected);
                var canInspectSystemIdentifier = reader.GetBoolean(2);
                var identityMatches =
                    string.Equals(reader.GetString(0), options.Contract.Database, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(1), login.Name, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(3), adminTarget.ServerAddress, StringComparison.Ordinal) &&
                    reader.GetInt32(4) == adminTarget.ServerPort;
                if (!identityMatches || await reader.ReadAsync(cancellationToken))
                    throw new BootstrapRejectedException(
                        "login_authentication_identity_mismatch", BootstrapExitCodes.AuthenticationRejected);
                await reader.CloseAsync();
                if (canInspectSystemIdentifier)
                {
                    await using var system = new NpgsqlCommand(
                        "SELECT system_identifier::text FROM pg_catalog.pg_control_system()", connection);
                    var identifier = Convert.ToString(await system.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture);
                    if (identifier is null || !CryptographicEquals(Convert.ToHexStringLower(
                            SHA256.HashData(Encoding.UTF8.GetBytes(identifier))),
                        options.Contract.SystemIdentifierSha256))
                        throw new BootstrapRejectedException(
                            "login_authentication_identity_mismatch", BootstrapExitCodes.AuthenticationRejected);
                }
            }
            catch (PostgresException exception) when (exception.SqlState == "28P01")
            {
                throw new BootstrapRejectedException(
                    "login_authentication_failed", BootstrapExitCodes.AuthenticationRejected);
            }
            catch (NpgsqlException)
            {
                throw new BootstrapRejectedException(
                    "login_authentication_failed", BootstrapExitCodes.AuthenticationRejected);
            }
        }
    }

    private async Task AuthenticateBackupAsync(
        NpgsqlConnectionStringBuilder adminBuilder,
        ManagedRole login,
        SensitivePassword password,
        CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = adminBuilder.Host,
            Port = adminBuilder.Port,
            Username = login.Name,
            Password = password.RevealForAuthentication(),
            SslMode = adminBuilder.SslMode,
            RootCertificate = adminBuilder.RootCertificate,
            SslCertificate = adminBuilder.SslCertificate,
            SslKey = adminBuilder.SslKey,
            CheckCertificateRevocation = adminBuilder.CheckCertificateRevocation,
            ApplicationName = "saydin-role-bootstrap-backup-auth-probe",
            Pooling = false,
            IncludeErrorDetail = false,
            LogParameters = false,
            PersistSecurityInfo = false,
            Timeout = Seconds(options.Timeouts.Connect),
            CommandTimeout = Seconds(options.Timeouts.Statement),
        };
        try
        {
            await using var replication = new PhysicalReplicationConnection(builder.ConnectionString);
            await replication.Open(cancellationToken);
            var identification = await replication.IdentifySystem(cancellationToken);
            var actualHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(identification.SystemId)));
            if (!CryptographicEquals(actualHash, options.Contract.SystemIdentifierSha256) ||
                !string.IsNullOrEmpty(identification.DbName))
                throw new BootstrapRejectedException(
                    "backup_authentication_identity_mismatch",
                    BootstrapExitCodes.AuthenticationRejected);
        }
        catch (BootstrapRejectedException)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState is "28P01" or "42501" or "53300")
        {
            throw new BootstrapRejectedException(
                "backup_authentication_failed", BootstrapExitCodes.AuthenticationRejected);
        }
        catch (NpgsqlException)
        {
            throw new BootstrapRejectedException(
                "backup_authentication_failed", BootstrapExitCodes.AuthenticationRejected);
        }
    }

    private async Task RejectSchedulerAuthenticationAsync(
        NpgsqlConnectionStringBuilder adminBuilder,
        CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Username = options.Contract.TimescaleScheduler.Name,
            Password = $"scheduler-probe-{Guid.NewGuid():N}-A9!",
            ApplicationName = "saydin-role-bootstrap-scheduler-negative-probe",
            Pooling = false,
            IncludeErrorDetail = false,
            Timeout = Seconds(options.Timeouts.Connect),
            CommandTimeout = Seconds(options.Timeouts.Statement),
        };
        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState is "28P01" or "53300")
        {
            return;
        }
        catch (NpgsqlException)
        {
            throw new BootstrapRejectedException(
                "scheduler_authentication_probe_failed", BootstrapExitCodes.AuthenticationRejected);
        }
        throw new BootstrapRejectedException(
            "scheduler_authentication_succeeded", BootstrapExitCodes.AuthenticationRejected);
    }

    private static async Task<string> FormatSqlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string formatQuery,
        CancellationToken cancellationToken,
        params string[] values)
    {
        await using var command = new NpgsqlCommand(formatQuery, connection, transaction);
        foreach (var value in values)
            command.Parameters.AddWithValue(value);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
                   CultureInfo.InvariantCulture) ??
               throw TopologyRejected("sql_format_failed");
    }

    private async Task ExecuteSqlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = Seconds(options.Timeouts.Statement),
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string value) =>
        '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string QuoteLiteral(string value) =>
        '\'' + value.Replace("'", "''", StringComparison.Ordinal) + '\'';

    private static string Bool(bool value) => value ? "TRUE" : "FALSE";

    private static BootstrapRejectedException TopologyRejected(string code) =>
        new(code, BootstrapExitCodes.TopologyRejected);
}

internal sealed record VerifiedAdminTarget(string Role, string ServerAddress, int ServerPort);
