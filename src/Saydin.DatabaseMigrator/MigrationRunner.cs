using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Saydin.DatabaseSecurity;
using Saydin.Migrations;

namespace Saydin.DatabaseMigrator;

internal sealed record MigrationRunResult(
    int Applied,
    int AlreadyApplied,
    int SkippedOptional,
    bool BackupPostBootstrapRequired = false);

internal interface IMigrationFaultInjector
{
    ValueTask AfterBodyAsync(
        MigrationDefinition migration,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken);

    ValueTask AfterCommitAsync(MigrationDefinition migration, CancellationToken cancellationToken);
}

internal sealed class NoMigrationFaultInjector : IMigrationFaultInjector
{
    public static NoMigrationFaultInjector Instance { get; } = new();

    public ValueTask AfterBodyAsync(
        MigrationDefinition migration,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask AfterCommitAsync(
        MigrationDefinition migration,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class MigrationRunner(
    MigratorOptions options,
    TextWriter output,
    IMigrationFaultInjector? faultInjector = null,
    bool allowCanonicalPrefixFixture = false)
{
    private const long AdvisoryLockKey = 23_050_527_783_118;
    private const int ControlVersion = 1;
    private const string OptionalExporterVersion = "012b_create_exporter_role";
    private const string IngestionLedgerVersion = "015_ingestion_windows";
    private const string IngestionWriteFenceVersion = "016_ingestion_write_fence";
    private const string AuthoritativeCalendarVersion = "017_authoritative_market_calendars";
    private const string ScenarioIntegrityVersion = "018_scenario_integrity";
    private const string PrivilegeSeparationVersion = "019_privilege_separation";
    private const string PriceAuthorityVersion = "020_price_authority_expand";
    private const string ApiTrustVersion = "021_api_trust_expand";
    private const string PrincipalRetentionVersion = "022_principal_retention";
    private const string ApiSecurityAdmissionVersion = "023_installation_lifecycle_admission";
    private const string CredentialRehashVersion = "024_installation_credential_rehash";
    private static readonly string[] LegacyVersions =
    [
        "001_initial",
        "002_add_assets",
        "003_switch_precious_metals_to_oxr",
        "004_add_inflation_rates",
        "005_add_tcmb_currencies",
        "006_scenario_type",
        "007_add_dca_scenario_type",
        "008_add_activity_logs",
        "008b_disable_activity_log_compression",
        "009_widen_activity_log_columns",
        "010_add_geo_columns",
        "011_phase2_schema_hardening",
        "012_faz3_schema",
        OptionalExporterVersion,
        "013_enable_activity_log_compression",
        "014_schema_migrations",
    ];

    // 001..014 are an immutable, already-deployed trust root. Database row
    // checksums protect managed databases, while these repository-pinned hashes
    // also protect first bootstrap and legacy baseline adoption.

    private readonly IMigrationFaultInjector _faultInjector =
        faultInjector ?? NoMigrationFaultInjector.Instance;

    public async Task<MigrationRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var manifest = MigrationManifest.Load(options.MigrationsDirectory);
        if (allowCanonicalPrefixFixture)
            ValidateCanonicalPrefixFixture(manifest);
        else
            ValidateTrustedPrefix(manifest);
        var trustedPrefixCount = MigratorMigrationTrustRoot.Versions.Count;
        var impacts = MigrationImpactSet.LoadAndVerify(
            manifest, trustedPrefixCount, options.ImpactConfiguration);
        var hasImpactTail = manifest.Migrations.Count > trustedPrefixCount;
        var requiresIngestionLedger = manifest.Migrations.Any(
            migration => migration.Version == IngestionLedgerVersion);
        var requiresIngestionWriteFence = manifest.Migrations.Any(
            migration => migration.Version == IngestionWriteFenceVersion);
        var requiresAuthoritativeCalendars = manifest.Migrations.Any(
            migration => migration.Version == AuthoritativeCalendarVersion);
        var requiresScenarioIntegrity = manifest.Migrations.Any(
            migration => migration.Version == ScenarioIntegrityVersion);
        var requiresPrivilegeSeparation = manifest.Migrations.Any(
            migration => migration.Version == PrivilegeSeparationVersion);
        var requiresPriceAuthority = manifest.Migrations.Any(
            migration => migration.Version == PriceAuthorityVersion);
        var requiresApiTrust = manifest.Migrations.Any(
            migration => migration.Version == ApiTrustVersion);
        var requiresPrincipalRetention = manifest.Migrations.Any(
            migration => migration.Version == PrincipalRetentionVersion);
        var requiresApiSecurityAdmission = manifest.Migrations.Any(
            migration => migration.Version == ApiSecurityAdmissionVersion);
        var requiresCredentialRehash = manifest.Migrations.Any(
            migration => migration.Version == CredentialRehashVersion);
        var sqlBodies = manifest.Migrations
            .Where(migration => migration.Kind == MigrationKind.Sql)
            .ToDictionary(
                migration => migration.Version,
                migration => MigratorMigrationTrustRoot.Checksums.ContainsKey(migration.Version) ||
                             impacts.For(migration.Version).Mode != MigrationExecutionMode.ResumableOnline
                    ? SqlScriptNormalizer.Normalize(migration)
                    : migration.ReadSql(),
                StringComparer.Ordinal);
        if (requiresPrivilegeSeparation)
        {
            sqlBodies["008_add_activity_logs"] =
                SqlScriptNormalizer.DeferPinnedActivityCompressionPolicy(
                    sqlBodies["008_add_activity_logs"], "008_add_activity_logs.sql",
                    "selectadd_compression_policy('activity_logs',interval'7 days')");
            sqlBodies["013_enable_activity_log_compression"] =
                SqlScriptNormalizer.DeferPinnedActivityCompressionPolicy(
                    sqlBodies["013_enable_activity_log_compression"],
                    "013_enable_activity_log_compression.sql",
                    "selectadd_compression_policy('activity_logs',interval'7 days',if_not_exists=>true)");
        }

        var connectionBuilder = options.LegacyPrivilegeCutover
            ? options.BuildLegacyAdminConnection()
            : options.BuildNormalConnection();
        await using var dataSource = NpgsqlDataSource.Create(connectionBuilder.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await SetDeterministicSearchPathAsync(connection, cancellationToken);
        var connectedTarget = await ReadTargetIdentityAsync(connection, cancellationToken);
        VerifyConnectedTarget(connectedTarget);
        var targetLockKey = ContractLockKey(options.Contract.TargetLockSha256);
        await AcquireLockAsync(connection, targetLockKey, "target", cancellationToken);
        try
        {
            var backupRequiredAtInvocationStart = await VerifySecuritySessionAndAssumeRoleAsync(
                connection, cancellationToken);
            var target = await ReadTargetIdentityAsync(connection, cancellationToken);
            await output.WriteLineAsync($"migration target: {options.SafeTarget}");
            await AcquireLockAsync(connection, AdvisoryLockKey, "migration", cancellationToken);
            var impactPreflightInProgress = false;
            try
            {
                var databaseState = await ClassifyAsync(connection, manifest, cancellationToken);
                var deferImpactPreflightToTail = hasImpactTail && databaseState == DatabaseState.Blank;
                if (hasImpactTail && databaseState != DatabaseState.Managed && !deferImpactPreflightToTail)
                    throw new MigratorRejectedException(
                        "migration_impact_requires_managed_terminal_predecessor");
                if (options.LegacyPrivilegeCutover)
                {
                    if (databaseState is not (DatabaseState.LegacyComplete014 or DatabaseState.Managed))
                        throw new MigratorRejectedException("legacy_cutover_state_rejected");
                    if (databaseState == DatabaseState.Managed)
                        await VerifyManagedCutoverThrough018Async(connection, manifest, cancellationToken);
                }
                else if (databaseState == DatabaseState.LegacyComplete014)
                {
                    throw new MigratorRejectedException("legacy_privilege_cutover_required");
                }
                switch (databaseState)
                {
                    case DatabaseState.Blank:
                        if (options.VerifyOnly)
                            throw new MigratorRejectedException("blank_database_not_ready");
                        await CreateControlPlaneAsync(
                            connection, "bootstrapping", manifest, legacyOptionalStatus: null, cancellationToken);
                        break;

                    case DatabaseState.LegacyComplete014:
                        await VerifyCoreSchemaAsync(
                            connection, requireIngestionLedger: false,
                            requireIngestionWriteFence: false,
                            requireAuthoritativeCalendars: false,
                            requireScenarioIntegrity: false,
                            requirePrivilegeSeparation: false,
                            requirePriceAuthority: false,
                            requireApiTrust: false,
                            requirePrincipalRetention: false,
                            requireApiSecurityAdmission: false,
                            requireCredentialRehash: false, cancellationToken);
                        var legacyOptionalStatus = await ValidateLegacyOptionalStepAsync(
                            connection, cancellationToken);
                        if (options.VerifyOnly)
                            throw new MigratorRejectedException("legacy_baseline_required");
                        await CreateControlPlaneAsync(
                            connection, "baselining", manifest, legacyOptionalStatus, cancellationToken);
                        await output.WriteLineAsync("legacy 014 baseline checksums recorded");
                        break;

                    case DatabaseState.Managed:
                        await ValidateManagedStateAsync(connection, manifest, cancellationToken);
                        break;

                    default:
                        throw new MigratorRejectedException("database_partial_or_ambiguous");
                }

                if (options.LegacyPrivilegeCutover &&
                    await IsMigrationTerminalAsync(
                        connection, PrivilegeSeparationVersion, cancellationToken))
                {
                    await AssumeOwnerRoleAfterLegacyCutoverAsync(connection, cancellationToken);
                    target = await ReadTargetIdentityAsync(connection, cancellationToken);
                }

                if (options.VerifyOnly)
                {
                    var control = await ReadControlAsync(connection, cancellationToken) ??
                        throw new MigratorRejectedException("migration_control_row_missing");
                    if (control.State != "ready")
                        throw new MigratorRejectedException("migration_control_not_ready");
                    await VerifyAllAppliedAsync(connection, manifest, cancellationToken);
                    await VerifyCoreSchemaAsync(
                        connection, requiresIngestionLedger, requiresIngestionWriteFence,
                        requiresAuthoritativeCalendars, requiresScenarioIntegrity,
                        requiresPrivilegeSeparation, requiresPriceAuthority, requiresApiTrust,
                        requiresPrincipalRetention, requiresApiSecurityAdmission,
                        requiresCredentialRehash,
                        cancellationToken);
                    await VerifyImpactPostconditionsAsync(
                        connection, manifest, impacts, trustedPrefixCount, cancellationToken);
                    return new MigrationRunResult(0, manifest.Migrations.Count, 0);
                }

                impactPreflightInProgress = hasImpactTail && !deferImpactPreflightToTail;
                var pendingImpactMigrations = new List<MigrationDefinition>();
                foreach (var migration in manifest.Migrations.Skip(trustedPrefixCount))
                {
                    var state = await ReadMigrationStateAsync(
                        connection, migration.Version, cancellationToken);
                    if (state?.State is not ("succeeded" or "skipped_optional"))
                        pendingImpactMigrations.Add(migration);
                }
                if (pendingImpactMigrations.Count > 1)
                    throw new MigratorRejectedException("migration_impact_multi_pending_rejected");
                if (pendingImpactMigrations is [var pendingImpact])
                {
                    if (!deferImpactPreflightToTail)
                        await VerifyImpactPreflightAsync(
                            connection, pendingImpact, impacts.For(pendingImpact.Version), cancellationToken);
                }
                impactPreflightInProgress = false;

                await SetControlStateAsync(connection, "bootstrapping", manifest.Checksum, null, cancellationToken);
                var applied = 0;
                var alreadyApplied = 0;
                var skippedOptional = 0;
                foreach (var migration in manifest.Migrations)
                {
                    var state = await ReadMigrationStateAsync(connection, migration.Version, cancellationToken);
                    if (state is not null)
                    {
                        EnsureChecksumMatches(migration, state);
                        if (state.State == "succeeded")
                        {
                            alreadyApplied++;
                            continue;
                        }
                        if (state.State == "skipped_optional" && migration.Kind == MigrationKind.OptionalExporterRole)
                        {
                            alreadyApplied++;
                            skippedOptional++;
                            continue;
                        }
                    }

                    var impact = MigratorMigrationTrustRoot.Checksums.ContainsKey(migration.Version)
                        ? null
                        : impacts.For(migration.Version);
                    if (impact is not null && deferImpactPreflightToTail)
                    {
                        await VerifyImpactPreflightAsync(
                            connection, migration, impact, cancellationToken);
                        deferImpactPreflightToTail = false;
                    }
                    var finalState = migration.Kind switch
                    {
                        MigrationKind.Sql when impact?.Mode == MigrationExecutionMode.ResumableOnline =>
                            await ApplyOnlineAsync(
                                connection, migration, impact, target, cancellationToken),
                        MigrationKind.Sql => await ApplySqlAsync(
                            connection, migration, sqlBodies[migration.Version], target,
                            impact, cancellationToken),
                        MigrationKind.OptionalExporterRole => await ApplyOptionalExporterAsync(
                            connection, migration, target, cancellationToken),
                        _ => throw new UnreachableException(),
                    };
                    applied++;
                    if (finalState == "skipped_optional")
                        skippedOptional++;
                    if (options.LegacyPrivilegeCutover &&
                        migration.Version == PrivilegeSeparationVersion)
                    {
                        await AssumeOwnerRoleAfterLegacyCutoverAsync(connection, cancellationToken);
                        target = await ReadTargetIdentityAsync(connection, cancellationToken);
                    }
                }

                await VerifyAllAppliedAsync(connection, manifest, cancellationToken);
                await VerifyCoreSchemaAsync(
                    connection, requiresIngestionLedger, requiresIngestionWriteFence,
                    requiresAuthoritativeCalendars, requiresScenarioIntegrity,
                    requiresPrivilegeSeparation, requiresPriceAuthority, requiresApiTrust,
                    requiresPrincipalRetention, requiresApiSecurityAdmission,
                    requiresCredentialRehash,
                    cancellationToken);
                await AssertTargetIdentityAsync(connection, target, cancellationToken);
                await SetControlStateAsync(connection, "ready", manifest.Checksum, null, cancellationToken);
                return new MigrationRunResult(
                    applied, alreadyApplied, skippedOptional,
                    BackupPostBootstrapRequired:
                        !backupRequiredAtInvocationStart && requiresPrincipalRetention);
            }
            catch (MigratorRejectedException ex)
            {
                if (!options.VerifyOnly && !impactPreflightInProgress)
                    await TrySetControlFailedAsync(connection, ex.Code, cancellationToken);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!options.VerifyOnly && !impactPreflightInProgress)
                    await TrySetControlFailedAsync(connection, FailureCode(ex), cancellationToken);
                throw new MigratorRejectedException("migration_run_failed", FailureCode(ex), ex);
            }
            finally
            {
                await TryReleaseLockAsync(connection, AdvisoryLockKey, cancellationToken);
            }
        }
        finally
        {
            await TryReleaseLockAsync(connection, targetLockKey, cancellationToken);
        }
    }

    private async Task VerifyImpactPreflightAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        CancellationToken cancellationToken)
    {
        var snapshot = await MigrationImpactPreflight.VerifyAsync(
            connection, options, migration, impact, cancellationToken);
        await output.WriteLineAsync(
            $"impact preflight: version={migration.Version}; " +
            $"relation_bytes={snapshot.RelationBytes}; compressed_bytes={snapshot.CompressedBytes}; " +
            $"free_bytes_after={snapshot.FreeBytesAfter}; " +
            $"headroom_bps={snapshot.HeadroomRatioBasisPoints}; " +
            $"waiting_locks={snapshot.WaitingLocks}; replicas={snapshot.StreamingReplicas}");
    }

    private static async Task<bool> IsPostBootstrapManagedDatabaseAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var controlsExist = await ScalarAsync<bool>(connection, """
            SELECT pg_catalog.to_regclass('public.saydin_migration_control') IS NOT NULL
               AND pg_catalog.to_regclass('public.saydin_role_contract') IS NOT NULL
               AND pg_catalog.to_regclass('public.schema_migrations') IS NOT NULL
            """, cancellationToken);
        if (!controlsExist) return false;
        return await ScalarAsync<bool>(connection, """
            SELECT (SELECT count(*)=1 AND bool_and(state='succeeded')
                      FROM public.schema_migrations
                     WHERE version='022_principal_retention')
            """, cancellationToken);
    }

    private async Task<bool> VerifySecuritySessionAndAssumeRoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var identity = new NpgsqlCommand("""
            SELECT current_database(), session_user, current_user, role.oid,
                   role.rolcanlogin, role.rolsuper, role.rolcreatedb, role.rolcreaterole,
                   role.rolinherit, role.rolreplication, role.rolbypassrls,
                   role.rolconnlimit, role.rolvaliduntil IS NULL, role.rolconfig IS NULL,
                   pg_catalog.shobj_description(role.oid, 'pg_authid'),
                   system_identifier::text, current_setting('search_path')
              FROM pg_catalog.pg_roles role
              CROSS JOIN pg_catalog.pg_control_system()
             WHERE role.rolname=session_user
            """, connection))
        {
            await using var reader = await identity.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new MigratorRejectedException("migrator_identity_unavailable");
            var database = reader.GetString(0);
            var sessionUser = reader.GetString(1);
            var currentUser = reader.GetString(2);
            var oid = reader.GetFieldValue<uint>(3);
            var canLogin = reader.GetBoolean(4);
            var superuser = reader.GetBoolean(5);
            var createDatabase = reader.GetBoolean(6);
            var createRole = reader.GetBoolean(7);
            var inherit = reader.GetBoolean(8);
            var replication = reader.GetBoolean(9);
            var bypassRls = reader.GetBoolean(10);
            var connectionLimit = reader.GetInt32(11);
            var validUntilIsNull = reader.GetBoolean(12);
            var configIsNull = reader.GetBoolean(13);
            var marker = reader.IsDBNull(14) ? null : reader.GetString(14);
            var systemIdentifier = reader.GetString(15);
            var searchPath = reader.GetString(16);
            var systemHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(systemIdentifier)));
            if (!string.Equals(database, options.Database, StringComparison.Ordinal) ||
                !string.Equals(sessionUser, currentUser, StringComparison.Ordinal) ||
                !CryptographicEquals(systemHash, options.Contract.SystemIdentifierSha256) ||
                searchPath != "pg_catalog,public,pg_temp" || await reader.ReadAsync(cancellationToken))
                throw new MigratorRejectedException("migrator_target_contract_mismatch");

            if (options.LegacyPrivilegeCutover)
            {
                if (oid != 10 || !canLogin || !superuser || !createDatabase || !createRole)
                    throw new MigratorRejectedException("legacy_bootstrap_admin_rejected");
            }
            else
            {
                var expectedLogin = options.Contract.Login(LoginPurpose.Migrator, options.LoginVersion);
                if (!string.Equals(sessionUser, options.ExpectedLogin, StringComparison.Ordinal) ||
                    !options.Contract.IsExactMarker(expectedLogin, marker ?? string.Empty) || !canLogin ||
                    superuser || createDatabase || createRole || inherit || replication || bypassRls ||
                    connectionLimit != -1 || !validUntilIsNull || !configIsNull)
                    throw new MigratorRejectedException("migrator_login_contract_mismatch");
            }
        }

        await VerifyCompleteRoleContractAsync(
            connection, requireBackup: false, allowBackup: true, cancellationToken);
        if (!options.LegacyPrivilegeCutover)
        {
            string setRoleSql;
            await using (var format = new NpgsqlCommand(
                             "SELECT pg_catalog.format('SET ROLE %I',$1)", connection))
            {
                format.Parameters.AddWithValue(options.Contract.Owner.Name);
                setRoleSql = Convert.ToString(await format.ExecuteScalarAsync(cancellationToken)) ??
                             throw new MigratorRejectedException("migrator_set_role_failed");
            }
            await using var setRole = new NpgsqlCommand(setRoleSql, connection);
            await setRole.ExecuteNonQueryAsync(cancellationToken);
            if (!string.Equals(await ScalarAsync<string>(connection, "SELECT current_user", cancellationToken),
                    options.Contract.Owner.Name, StringComparison.Ordinal))
                throw new MigratorRejectedException("migrator_set_role_failed");
        }
        var requireBackup = await IsPostBootstrapManagedDatabaseAsync(connection, cancellationToken);
        await VerifyCompleteRoleContractAsync(
            connection, requireBackup, allowBackup: requireBackup, cancellationToken);
        return requireBackup;
    }

    private async Task VerifyCompleteRoleContractAsync(
        NpgsqlConnection connection,
        bool requireBackup,
        bool allowBackup,
        CancellationToken cancellationToken)
    {
        var required = options.Contract.StableRoles
            .Concat(requireBackup
                ? [options.Contract.BackupLogin(1, options.BackupV1ValidUntilUtc)]
                : [])
            .Concat([options.Contract.Login(LoginPurpose.Migrator, options.LoginVersion)])
            .DistinctBy(role => role.Name, StringComparer.Ordinal)
            .ToDictionary(role => role.Name, StringComparer.Ordinal);
        var observed = new Dictionary<string, ManagedRole>(StringComparer.Ordinal);
        var roleRows = new List<(string Name, bool CanLogin, bool Superuser,
            bool CreateDatabase, bool CreateRole, bool Inherit, bool Replication,
            bool BypassRls, int ConnectionLimit, string? ValidUntilUtc,
            bool ConfigIsNull, string Marker)>();
        await using (var roles = new NpgsqlCommand("""
            SELECT role.rolname, role.rolcanlogin, role.rolsuper, role.rolcreatedb,
                   role.rolcreaterole, role.rolinherit, role.rolreplication,
                   role.rolbypassrls, role.rolconnlimit,
                   CASE WHEN role.rolvaliduntil IS NULL THEN NULL ELSE
                       pg_catalog.to_char(role.rolvaliduntil AT TIME ZONE 'UTC',
                           'YYYY-MM-DD"T"HH24:MI:SS"Z"') END,
                   role.rolconfig IS NULL,
                   pg_catalog.shobj_description(role.oid, 'pg_authid')
              FROM pg_catalog.pg_roles role
             WHERE pg_catalog.left(role.rolname,pg_catalog.length($1)+1)=$1||'_'
             ORDER BY role.rolname COLLATE "C"
            """, connection))
        {
            roles.Parameters.AddWithValue(options.Contract.Prefix);
            await using var reader = await roles.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                roleRows.Add((
                    reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2),
                    reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.GetBoolean(6), reader.GetBoolean(7), reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetBoolean(10),
                    reader.IsDBNull(11) ? string.Empty : reader.GetString(11)));
            }
        }

        var resolvedRows = new List<(ManagedRole Role, bool CanLogin, bool Superuser,
            bool CreateDatabase, bool CreateRole, bool Inherit, bool Replication,
            bool BypassRls, int ConnectionLimit, string? ValidUntilUtc,
            bool ConfigIsNull)>();
        foreach (var row in roleRows)
        {
            ManagedRole? role = required.GetValueOrDefault(row.Name);
            if (role is null && options.Contract.TryResolveManagedMarker(row.Marker, out var resolved))
            {
                role = !allowBackup && resolved.Purpose == "backup" ? null : resolved;
                if (role is not null &&
                    !string.Equals(role.Name, row.Name, StringComparison.Ordinal))
                    role = null;
            }
            if (role is null || !observed.TryAdd(row.Name, role)
                || !options.Contract.IsExactMarker(role, row.Marker))
                throw new MigratorRejectedException("managed_role_contract_mismatch");
            resolvedRows.Add((role, row.CanLogin, row.Superuser, row.CreateDatabase,
                row.CreateRole, row.Inherit, row.Replication, row.BypassRls,
                row.ConnectionLimit, row.ValidUntilUtc, row.ConfigIsNull));
        }
        if (required.Keys.Any(name => !observed.ContainsKey(name)))
            throw new MigratorRejectedException("managed_role_contract_mismatch");

        var currentVersions = resolvedRows
            .Where(row => row.Role.Kind == ManagedRoleKind.Login
                && row.Role.LoginVersion is not null
                && row.Role.Purpose is not ("backup" or "timescale_scheduler"))
            .GroupBy(row => row.Role.Purpose, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Max(row => row.Role.LoginVersion!.Value),
                StringComparer.Ordinal);
        if (Enum.GetValues<LoginPurpose>().Select(RoleContract.PurposeName)
            .Any(purpose => !currentVersions.ContainsKey(purpose)))
            throw new MigratorRejectedException("managed_role_contract_mismatch");

        foreach (var row in resolvedRows)
        {
            var role = row.Role;
            var isDrainingOldLogin = role.LoginVersion is { } version
                && currentVersions.TryGetValue(role.Purpose, out var currentVersion)
                && version < currentVersion;
            var loginStateValid = role.Kind != ManagedRoleKind.Login
                ? !row.CanLogin
                : row.CanLogin || isDrainingOldLogin;
            var expectedValidUntil = role.ValidUntilUtc is null
                ? null
                : RoleContract.FormatBackupValidUntil(role.ValidUntilUtc.Value);
            if (!loginStateValid || row.Superuser || row.CreateDatabase || row.CreateRole
                || row.Inherit || row.Replication != role.Replication || row.BypassRls
                || row.ConnectionLimit != role.ConnectionLimit
                || !string.Equals(row.ValidUntilUtc, expectedValidUntil, StringComparison.Ordinal)
                || !row.ConfigIsNull)
                throw new MigratorRejectedException("managed_role_contract_mismatch");
        }

        await VerifyAllManagedMembershipsAsync(connection, observed.Values, cancellationToken);
        if (requireBackup)
            await VerifyBackupIsolationAndAvailabilityAsync(
                connection, observed.Values, cancellationToken);

        await using (var database = new NpgsqlCommand("""
            SELECT pg_catalog.pg_get_userbyid(datdba)
              FROM pg_catalog.pg_database WHERE datname=current_database()
            """, connection))
        {
            if (!string.Equals(Convert.ToString(await database.ExecuteScalarAsync(cancellationToken)),
                    options.Contract.Owner.Name, StringComparison.Ordinal))
                throw new MigratorRejectedException("database_owner_contract_mismatch");
        }

        await VerifyDatabaseControlPlaneAsync(connection, cancellationToken);

        await using var extensions = new NpgsqlCommand("""
            SELECT extname,extversion,extowner
              FROM pg_catalog.pg_extension
             WHERE extname IN ('timescaledb','uuid-ossp')
             ORDER BY extname COLLATE "C"
            """, connection);
        await using var extensionReader = await extensions.ExecuteReaderAsync(cancellationToken);
        var extensionObserved = new List<(string Name, string Version, uint Owner)>();
        while (await extensionReader.ReadAsync(cancellationToken))
            extensionObserved.Add((extensionReader.GetString(0), extensionReader.GetString(1),
                extensionReader.GetFieldValue<uint>(2)));
        if (!extensionObserved.SequenceEqual(new[]
            {
                ("timescaledb", options.TimescaleVersion, 10U),
                ("uuid-ossp", options.UuidOsspVersion, 10U),
            }))
            throw new MigratorRejectedException("extension_role_contract_mismatch");
    }

    private async Task AssumeOwnerRoleAfterLegacyCutoverAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var current = await ScalarAsync<string>(connection, "SELECT current_user", cancellationToken);
        if (string.Equals(current, options.Contract.Owner.Name, StringComparison.Ordinal))
            return;
        string setRoleSql;
        await using (var format = new NpgsqlCommand(
                         "SELECT pg_catalog.format('SET ROLE %I',$1)", connection))
        {
            format.Parameters.AddWithValue(options.Contract.Owner.Name);
            setRoleSql = Convert.ToString(await format.ExecuteScalarAsync(cancellationToken)) ??
                         throw new MigratorRejectedException("legacy_owner_transition_failed");
        }
        await using var setRole = new NpgsqlCommand(setRoleSql, connection);
        await setRole.ExecuteNonQueryAsync(cancellationToken);
        if (!string.Equals(
                await ScalarAsync<string>(connection, "SELECT current_user", cancellationToken),
                options.Contract.Owner.Name, StringComparison.Ordinal))
            throw new MigratorRejectedException("legacy_owner_transition_failed");
    }

    private async Task VerifyDatabaseControlPlaneAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await AssertSecurityFingerprintAsync(connection, "database_acl_set_mismatch", """
            WITH expected(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ($1,$1,'CONNECT',false),($1,$1,'CREATE',false),($1,$1,'TEMPORARY',false),
                ($2,$1,'CONNECT',false),($3,$1,'CONNECT',false),($4,$1,'CONNECT',false),
                ($5,$1,'CONNECT',false),($6,$1,'CONNECT',false),($7,$1,'CONNECT',false),
                ($8,$1,'CONNECT',false)),
            actual AS (
                SELECT coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM pg_catalog.pg_database database
                  CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(database.datacl,
                      pg_catalog.acldefault('d',database.datdba))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE database.datname=pg_catalog.current_database()),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken,
            options.Contract.Owner.Name, options.Contract.MigratorCapability.Name,
            options.Contract.ApiCapability.Name, options.Contract.IngestionCapability.Name,
            options.Contract.CalendarImporterCapability.Name, options.Contract.ExporterCapability.Name,
            options.Contract.AuditCapability.Name, options.Contract.TimescaleScheduler.Name);

        await AssertSecurityFingerprintAsync(connection, "schema_acl_set_mismatch", """
            WITH expected(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ('pg_database_owner','pg_database_owner','CREATE',false),
                ('pg_database_owner','pg_database_owner','USAGE',false),
                ($1,'pg_database_owner','USAGE',false),($2,'pg_database_owner','USAGE',false),
                ($3,'pg_database_owner','USAGE',false),($4,'pg_database_owner','USAGE',false),
                ($5,'pg_database_owner','USAGE',false)),
            actual AS (
                SELECT coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM pg_catalog.pg_namespace namespace
                  CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(namespace.nspacl,
                      pg_catalog.acldefault('n',namespace.nspowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE namespace.nspname='public'),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken,
            options.Contract.ApiCapability.Name, options.Contract.IngestionCapability.Name,
            options.Contract.CalendarImporterCapability.Name, options.Contract.AuditCapability.Name,
            options.Contract.TimescaleScheduler.Name);

        await AssertSecurityFingerprintAsync(connection, "pg_control_acl_mismatch", """
            WITH target AS (
                SELECT function.oid,function.proowner,owner.rolname AS owner_name,
                       namespace.nspname
                  FROM pg_catalog.pg_proc function
                  JOIN pg_catalog.pg_namespace namespace ON namespace.oid=function.pronamespace
                  JOIN pg_catalog.pg_roles owner ON owner.oid=function.proowner
                 WHERE function.oid='pg_catalog.pg_control_system()'::pg_catalog.regprocedure),
            expected(grantee,grantor,privilege_type,is_grantable) AS (
                SELECT target.owner_name,target.owner_name,'EXECUTE',false FROM target
                UNION ALL SELECT $1,target.owner_name,'EXECUTE',false FROM target
                UNION ALL SELECT $2,target.owner_name,'EXECUTE',false FROM target
                UNION ALL SELECT $3,target.owner_name,'EXECUTE',false FROM target),
            actual AS (
                SELECT coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM target
                  CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(
                      (SELECT proacl FROM pg_catalog.pg_proc WHERE oid=target.oid),
                      pg_catalog.acldefault('f',target.proowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT (SELECT count(*)=1 AND bool_and(proowner=10 AND nspname='pg_catalog') FROM target)
               AND NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.MigratorCapability.Name, options.Contract.AuditCapability.Name);

        await AssertSecurityFingerprintAsync(connection, "pg_parameter_acl_mismatch", """
            WITH expected(grantee,grantor,privilege_type,is_grantable) AS (
                SELECT NULL::text,NULL::text,NULL::text,NULL::boolean WHERE false),
            actual AS (
                SELECT coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM pg_catalog.pg_parameter_acl parameter_acl
                 CROSS JOIN LATERAL pg_catalog.aclexplode(parameter_acl.paracl) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE parameter_acl.parname='session_replication_role'),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken);
    }

    private async Task VerifyAllManagedMembershipsAsync(
        NpgsqlConnection connection,
        IEnumerable<ManagedRole> managedRoles,
        CancellationToken cancellationToken)
    {
        var managed = managedRoles.ToArray();
        var names = managed.Select(role => role.Name).ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT granted.rolname, member.rolname, grantor.oid,
                   membership.admin_option, membership.inherit_option, membership.set_option
              FROM pg_catalog.pg_auth_members membership
              JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
              JOIN pg_catalog.pg_roles member ON member.oid=membership.member
              JOIN pg_catalog.pg_roles grantor ON grantor.oid=membership.grantor
             WHERE member.rolname=ANY($1) OR granted.rolname=ANY($1)
             ORDER BY granted.rolname COLLATE "C", member.rolname COLLATE "C", grantor.oid
            """, connection);
        command.Parameters.AddWithValue(names);
        var rows = new List<(string Granted, string Member, uint Grantor, bool Admin, bool Inherit, bool Set)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetFieldValue<uint>(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        var expected = managed.Where(role => role.Kind == ManagedRoleKind.Login &&
                                           role.Purpose is not ("timescale_scheduler" or "backup"))
            .SelectMany(role =>
            {
                var purpose = ParseLoginPurpose(role.Purpose);
                var memberships = new List<(string Granted, string Member, uint Grantor,
                    bool Admin, bool Inherit, bool Set)>
                {
                    (options.Contract.Capability(purpose).Name, role.Name, 10U, false, true, false),
                };
                if (purpose == LoginPurpose.Migrator)
                    memberships.Add((options.Contract.Owner.Name, role.Name, 10U, false, false, true));
                return memberships;
            })
            .Append((Granted: options.Contract.TimescaleScheduler.Name,
                Member: options.Contract.Owner.Name, Grantor: 10U,
                Admin: false, Inherit: false, Set: true))
            .Append((Granted: "pg_monitor", Member: options.Contract.ExporterCapability.Name,
                Grantor: 10U, Admin: false, Inherit: true, Set: false))
            .OrderBy(row => row.Granted, StringComparer.Ordinal)
            .ThenBy(row => row.Member, StringComparer.Ordinal)
            .ThenBy(row => row.Grantor)
            .ToArray();
        if (!rows.SequenceEqual(expected))
            throw new MigratorRejectedException("managed_role_membership_contract_mismatch");
    }

    private async Task VerifyBackupIsolationAndAvailabilityAsync(
        NpgsqlConnection connection,
        IEnumerable<ManagedRole> managedRoles,
        CancellationToken cancellationToken)
    {
        var backups = managedRoles.Where(role => role.Purpose == "backup").ToArray();
        if (backups.Length is < 1 or > 2 || backups.All(role => role.LoginVersion != 1) ||
            backups.Select(role => role.LoginVersion).Distinct().Count() != backups.Length)
            throw new MigratorRejectedException("backup_role_version_set_mismatch");

        var epoch = await ScalarAsync<long>(connection,
            "SELECT extract(epoch FROM pg_catalog.clock_timestamp())::bigint",
            cancellationToken);
        var now = DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (backups.All(role => role.ValidUntilUtc!.Value < now.AddHours(24)))
            throw new MigratorRejectedException("backup_role_rotation_horizon_insufficient");

        foreach (var backup in backups)
        {
            await AssertSecurityFingerprintAsync(connection, "backup_effective_acl_mismatch", """
                SELECT NOT pg_catalog.has_database_privilege($1,current_database(),'CONNECT')
                   AND NOT pg_catalog.has_database_privilege($1,current_database(),'CREATE')
                   AND NOT pg_catalog.has_database_privilege($1,current_database(),'TEMPORARY')
                   AND NOT pg_catalog.has_schema_privilege($1,'public','USAGE')
                   AND NOT pg_catalog.has_schema_privilege($1,'public','CREATE')
                   AND NOT pg_catalog.has_function_privilege(
                       $1,'pg_catalog.pg_control_system()','EXECUTE')
                """, cancellationToken, backup.Name);
            await AssertSecurityFingerprintAsync(connection,
                "backup_direct_acl_or_ownership_detected", """
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
                """, cancellationToken, backup.Name);
        }
    }

    private static LoginPurpose ParseLoginPurpose(string purpose) => purpose switch
    {
        "migrator" => LoginPurpose.Migrator,
        "api" => LoginPurpose.Api,
        "ingestion" => LoginPurpose.Ingestion,
        "calendar_importer" => LoginPurpose.CalendarImporter,
        "exporter" => LoginPurpose.Exporter,
        "audit" => LoginPurpose.Audit,
        _ => throw new MigratorRejectedException("managed_role_contract_mismatch"),
    };

    private static async Task VerifyManagedCutoverThrough018Async(
        NpgsqlConnection connection,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        await ValidateManagedStateAsync(connection, manifest, cancellationToken);
        if (manifest.Migrations.All(migration =>
                migration.Version != PrivilegeSeparationVersion))
            return;

        var control = await ReadControlAsync(connection, cancellationToken) ??
                      throw new MigratorRejectedException("migration_control_row_missing");
        var required = manifest.Migrations.TakeWhile(
            migration => migration.Version != PrivilegeSeparationVersion).ToArray();
        if (required.Length == 0 || required[^1].Version != ScenarioIntegrityVersion ||
            control.State is not ("ready" or "bootstrapping"))
            throw new MigratorRejectedException("legacy_cutover_managed_state_rejected");
        var rows = await ReadMigrationStatesAsync(connection, cancellationToken);
        var committedPrivilegeSeparation = rows.Count == required.Length + 1 &&
            rows.SingleOrDefault(row => row.Version == PrivilegeSeparationVersion) is
            { State: "succeeded" } privilegeRow &&
            string.Equals(privilegeRow.Checksum,
                manifest.Migrations.Single(migration =>
                    migration.Version == PrivilegeSeparationVersion).Checksum,
                StringComparison.Ordinal);
        if (rows.Count != required.Length && !committedPrivilegeSeparation)
            throw new MigratorRejectedException("legacy_cutover_managed_state_rejected");
        foreach (var migration in required)
        {
            var row = rows.SingleOrDefault(candidate => candidate.Version == migration.Version);
            if (row is null || row.State is not ("succeeded" or "skipped_optional"))
                throw new MigratorRejectedException("legacy_cutover_managed_state_rejected");
            EnsureChecksumMatches(migration, row);
        }
        if (committedPrivilegeSeparation)
        {
            var privilegeMigration = manifest.Migrations.Single(migration =>
                migration.Version == PrivilegeSeparationVersion);
            EnsureChecksumMatches(privilegeMigration,
                rows.Single(row => row.Version == PrivilegeSeparationVersion));
        }
    }

    private static async Task VerifyImpactPostconditionsAsync(
        NpgsqlConnection connection,
        MigrationManifest manifest,
        MigrationImpactSet impacts,
        int trustedPrefixCount,
        CancellationToken cancellationToken)
    {
        foreach (var migration in manifest.Migrations.Skip(trustedPrefixCount))
        {
            var impact = impacts.For(migration.Version);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);
            await using (var readOnly = new NpgsqlCommand(
                             "SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            await MigrationImpactPreflight.VerifyPostconditionsAsync(
                connection, transaction, impact, cancellationToken);
            if (impact.Mode == MigrationExecutionMode.ResumableOnline)
            {
                await using var checkpoint = new NpgsqlCommand("""
                    SELECT count(*)=1 AND bool_and(state='succeeded' AND manifest_sha256=$2)
                      FROM public.saydin_online_migration_checkpoints
                     WHERE migration_version=$1
                    """, connection, transaction);
                checkpoint.Parameters.AddWithValue(migration.Version);
                checkpoint.Parameters.AddWithValue(impact.ManifestSha256);
                if (await checkpoint.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new MigratorRejectedException(
                        "migration_online_checkpoint_not_terminal", migration.Version);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task<string> ApplySqlAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        string sql,
        TargetIdentity target,
        MigrationImpactDefinition? impact,
        CancellationToken cancellationToken)
    {
        await MarkRunningAsync(connection, migration, cancellationToken);
        var commitAttempted = false;
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await SetTransactionTimeoutsAsync(connection, transaction, cancellationToken);
            if (migration.Version == PrivilegeSeparationVersion)
                await SetPrivilegeContractAsync(connection, transaction, cancellationToken);
            await using (var body = new NpgsqlCommand(sql, connection, transaction)
            {
                CommandTimeout = Math.Max(1, (int)Math.Ceiling(options.Timeouts.Command.TotalSeconds)),
            })
            {
                await body.ExecuteNonQueryAsync(cancellationToken);
            }

            await _faultInjector.AfterBodyAsync(
                migration, connection, transaction, cancellationToken);
            if (impact is not null)
                await MigrationImpactPreflight.VerifyPostconditionsAsync(
                    connection, transaction, impact, cancellationToken);
            await MarkTerminalAsync(
                connection, transaction, migration, "succeeded", cancellationToken);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            await _faultInjector.AfterCommitAsync(migration, cancellationToken);
            await AssertTargetIdentityAsync(connection, target, cancellationToken);
            await output.WriteLineAsync($"applied: {migration.FileName}");
            return "succeeded";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (commitAttempted &&
                await TryReconcileCommittedAsync(connection, migration, cancellationToken))
            {
                await output.WriteLineAsync($"reconciled commit: {migration.FileName}");
                return "succeeded";
            }

            await TryMarkFailedAsync(connection, migration, FailureCode(ex), cancellationToken);
            await output.WriteLineAsync($"failed: {migration.FileName}; code={FailureCode(ex)}");
            throw new MigratorRejectedException("migration_failed", migration.Version, ex);
        }
    }

    private async Task<string> ApplyOnlineAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        TargetIdentity target,
        CancellationToken cancellationToken)
    {
        await MarkRunningAsync(connection, migration, cancellationToken);
        try
        {
            var result = await new OnlineMigrationExecutor(
                    output, _faultInjector, options.Contract.Owner.Name,
                    options.Contract.TimescaleScheduler.Name)
                .ExecuteAsync(connection, migration, impact, cancellationToken);
            await AssertTargetIdentityAsync(connection, target, cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (await TryReconcileCommittedAsync(connection, migration, cancellationToken))
                return "succeeded";
            await TryMarkFailedAsync(
                connection, migration, FailureCode(exception), cancellationToken);
            throw new MigratorRejectedException(
                "migration_online_failed", migration.Version, exception);
        }
    }

    private async Task<string> ApplyOptionalExporterAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        TargetIdentity target,
        CancellationToken cancellationToken)
    {
        await MarkRunningAsync(connection, migration, cancellationToken);
        var commitAttempted = false;
        const string finalState = "skipped_optional";
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await SetTransactionTimeoutsAsync(connection, transaction, cancellationToken);
            await _faultInjector.AfterBodyAsync(
                migration, connection, transaction, cancellationToken);
            await MarkTerminalAsync(
                connection, transaction, migration, finalState, cancellationToken);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            await _faultInjector.AfterCommitAsync(migration, cancellationToken);
            await AssertTargetIdentityAsync(connection, target, cancellationToken);
            await output.WriteLineAsync($"skipped optional: {migration.FileName}");
            return finalState;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (commitAttempted &&
                await TryReconcileCommittedAsync(connection, migration, cancellationToken))
            {
                return finalState;
            }

            await TryMarkFailedAsync(connection, migration, FailureCode(ex), cancellationToken);
            await output.WriteLineAsync($"failed: {migration.FileName}; code={FailureCode(ex)}");
            throw new MigratorRejectedException("migration_failed", migration.Version, ex);
        }
    }

    private async Task<DatabaseState> ClassifyAsync(
        NpgsqlConnection connection,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var schemaMigrationsExists = await ScalarAsync<bool>(connection,
            "SELECT to_regclass('public.schema_migrations') IS NOT NULL", cancellationToken);
        var controlExists = await ScalarAsync<bool>(connection,
            "SELECT to_regclass('public.saydin_migration_control') IS NOT NULL", cancellationToken);

        if (!schemaMigrationsExists)
        {
            if (controlExists)
                return DatabaseState.Ambiguous;
            var expectedObjectExists = await ScalarAsync<bool>(connection, """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public'
                      AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
                ) OR EXISTS (
                    SELECT 1
                    FROM pg_type t
                    JOIN pg_namespace n ON n.oid=t.typnamespace
                    WHERE n.nspname='public'
                      AND t.typtype IN ('e','d')
                      AND NOT EXISTS (
                          SELECT 1 FROM pg_depend d
                          WHERE d.classid='pg_type'::regclass AND d.objid=t.oid AND d.deptype='e')
                ) OR EXISTS (
                    SELECT 1
                    FROM pg_proc p
                    JOIN pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='public'
                      AND NOT EXISTS (
                          SELECT 1 FROM pg_depend d
                          WHERE d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.deptype='e')
                )
                """, cancellationToken);
            return expectedObjectExists ? DatabaseState.Ambiguous : DatabaseState.Blank;
        }

        if (controlExists)
            return DatabaseState.Managed;

        var checksumColumnExists = await ScalarAsync<bool>(connection, """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'schema_migrations'
                  AND column_name = 'checksum')
            """, cancellationToken);
        if (!checksumColumnExists)
            return DatabaseState.Ambiguous;
        var managedStateColumnExists = await ScalarAsync<bool>(connection, """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'schema_migrations'
                  AND column_name = 'state')
            """, cancellationToken);
        if (managedStateColumnExists)
            return DatabaseState.Ambiguous;

        var rows = await ReadLegacyRowsAsync(connection, cancellationToken);
        var versionsMatch = rows.Select(row => row.Version).SequenceEqual(LegacyVersions, StringComparer.Ordinal);
        var allChecksumsNull = rows.All(row => row.Checksum is null);
        return versionsMatch && allChecksumsNull && manifest.Migrations.Count >= LegacyVersions.Length
            ? DatabaseState.LegacyComplete014
            : DatabaseState.Ambiguous;
    }

    private static async Task CreateControlPlaneAsync(
        NpgsqlConnection connection,
        string initialState,
        MigrationManifest manifest,
        ExporterRoleStatus? legacyOptionalStatus,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var searchPath = new NpgsqlCommand(
                         // Omitting pg_catalog makes PostgreSQL search it implicitly before
                         // public, while keeping public as the target for historical unqualified DDL.
                         "SELECT pg_catalog.set_config('search_path','public,pg_temp',true)",
                         connection, transaction))
            await searchPath.ExecuteNonQueryAsync(cancellationToken);
        await AssertHistoricalTransactionSearchPathAsync(
            connection, transaction, cancellationToken);
        const string sql = """
            CREATE TABLE IF NOT EXISTS public.schema_migrations (
                version text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now(),
                checksum text NULL
            );
            ALTER TABLE public.schema_migrations ADD COLUMN IF NOT EXISTS state text;
            ALTER TABLE public.schema_migrations ADD COLUMN IF NOT EXISTS error_code text;
            ALTER TABLE public.schema_migrations ADD COLUMN IF NOT EXISTS started_at timestamptz;
            ALTER TABLE public.schema_migrations ADD COLUMN IF NOT EXISTS completed_at timestamptz;
            UPDATE public.schema_migrations
            SET state = COALESCE(state, 'succeeded'),
                completed_at = COALESCE(completed_at, applied_at)
            WHERE state IS NULL OR completed_at IS NULL;
            ALTER TABLE public.schema_migrations ALTER COLUMN state SET DEFAULT 'succeeded';
            ALTER TABLE public.schema_migrations ALTER COLUMN state SET NOT NULL;
            DO $control$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid='public.schema_migrations'::regclass
                      AND conname='chk_schema_migrations_state'
                ) THEN
                    ALTER TABLE public.schema_migrations ADD CONSTRAINT chk_schema_migrations_state
                    CHECK (state IN ('running', 'succeeded', 'skipped_optional', 'failed'));
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid='public.schema_migrations'::regclass
                      AND conname='chk_schema_migrations_checksum'
                ) THEN
                    ALTER TABLE public.schema_migrations ADD CONSTRAINT chk_schema_migrations_checksum
                    CHECK (checksum IS NULL OR checksum ~ '^[0-9a-f]{64}$');
                END IF;
            END
            $control$;

            CREATE TABLE IF NOT EXISTS public.saydin_migration_control (
                singleton smallint PRIMARY KEY CHECK (singleton = 1),
                control_version integer NOT NULL,
                state text NOT NULL CHECK (state IN ('baselining', 'bootstrapping', 'ready', 'failed')),
                manifest_checksum text NOT NULL,
                last_error_code text NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
            await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.saydin_migration_control
                (singleton, control_version, state, manifest_checksum, last_error_code, updated_at)
            VALUES (1, $1, $2, $3, NULL, now())
            ON CONFLICT (singleton) DO NOTHING
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(ControlVersion);
            command.Parameters.AddWithValue(initialState);
            command.Parameters.AddWithValue(manifest.Checksum);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (legacyOptionalStatus.HasValue)
        {
            foreach (var migration in manifest.Migrations.Take(LegacyVersions.Length))
            {
                var state = migration.Version == OptionalExporterVersion &&
                            legacyOptionalStatus == ExporterRoleStatus.Absent
                    ? "skipped_optional"
                    : "succeeded";
                await using var command = new NpgsqlCommand("""
                    UPDATE public.schema_migrations
                    SET checksum = $1,
                        state = $2,
                        error_code = NULL,
                        completed_at = COALESCE(completed_at, applied_at)
                    WHERE version = $3
                    """, connection, transaction);
                command.Parameters.AddWithValue(migration.Checksum);
                command.Parameters.AddWithValue(state);
                command.Parameters.AddWithValue(migration.Version);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new MigratorRejectedException("legacy_baseline_row_missing", migration.Version);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ValidateManagedStateAsync(
        NpgsqlConnection connection,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var control = await ReadControlAsync(connection, cancellationToken) ??
            throw new MigratorRejectedException("migration_control_row_missing");
        if (control.ControlVersion != ControlVersion)
            throw new MigratorRejectedException("migration_control_version_unknown");

        var rows = await ReadMigrationStatesAsync(connection, cancellationToken);
        var known = manifest.Migrations.ToDictionary(migration => migration.Version, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!known.TryGetValue(row.Version, out var migration))
                throw new MigratorRejectedException("schema_version_unknown", row.Version);
            EnsureChecksumMatches(migration, row);
        }

        var firstNonTerminal = -1;
        for (var index = 0; index < manifest.Migrations.Count; index++)
        {
            var row = rows.FirstOrDefault(candidate => candidate.Version == manifest.Migrations[index].Version);
            var terminal = row?.State is "succeeded" or "skipped_optional";
            if (!terminal && firstNonTerminal < 0)
                firstNonTerminal = index;
            if (terminal && firstNonTerminal >= 0)
                throw new MigratorRejectedException("migration_history_not_prefix", row!.Version);
        }

        if (control.State == "ready" && rows.Any(row => row.State is "running" or "failed"))
            throw new MigratorRejectedException("migration_control_state_inconsistent");
    }

    private static async Task VerifyAllAppliedAsync(
        NpgsqlConnection connection,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await ReadMigrationStatesAsync(connection, cancellationToken);
        if (rows.Count != manifest.Migrations.Count)
            throw new MigratorRejectedException("migration_count_mismatch");
        foreach (var migration in manifest.Migrations)
        {
            var row = rows.SingleOrDefault(candidate => candidate.Version == migration.Version) ??
                throw new MigratorRejectedException("migration_missing", migration.Version);
            EnsureChecksumMatches(migration, row);
            if (row.State is not ("succeeded" or "skipped_optional"))
                throw new MigratorRejectedException("migration_not_terminal", migration.Version);
            if (row.State == "skipped_optional" && migration.Kind != MigrationKind.OptionalExporterRole)
                throw new MigratorRejectedException("optional_state_invalid", migration.Version);
        }
    }

    private async Task VerifyCoreSchemaAsync(
        NpgsqlConnection connection,
        bool requireIngestionLedger,
        bool requireIngestionWriteFence,
        bool requireAuthoritativeCalendars,
        bool requireScenarioIntegrity,
        bool requirePrivilegeSeparation,
        bool requirePriceAuthority,
        bool requireApiTrust,
        bool requirePrincipalRetention,
        bool requireApiSecurityAdmission,
        bool requireCredentialRehash,
        CancellationToken cancellationToken)
    {
        var checks = new (string Code, string Sql)[]
        {
            ("core_tables_missing", """
                SELECT NOT EXISTS (
                    SELECT required.name
                    FROM unnest(ARRAY['assets','price_points','ingestion_jobs','users',
                                      'saved_scenarios','market_holidays','inflation_rates',
                                      'activity_logs']) required(name)
                    WHERE to_regclass('public.' || required.name) IS NULL)
                """),
            ("hypertables_missing", """
                SELECT COUNT(*) = 2
                FROM timescaledb_information.hypertables
                WHERE hypertable_schema = 'public'
                  AND hypertable_name IN ('price_points', 'activity_logs')
                """),
            ("activity_compression_disabled", """
                SELECT COALESCE(bool_and(compression_enabled), false)
                FROM timescaledb_information.hypertables
                WHERE hypertable_schema = 'public' AND hypertable_name = 'activity_logs'
                """),
            ("activity_compression_policy_missing", """
                SELECT EXISTS (
                    SELECT 1 FROM timescaledb_information.jobs
                    WHERE hypertable_schema = 'public' AND hypertable_name = 'activity_logs'
                      AND proc_name = 'policy_compression')
                """),
            ("ingestion_source_column_missing", """
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema='public' AND table_name='ingestion_jobs'
                      AND column_name='source' AND character_maximum_length=30)
                """),
            ("ingestion_asset_still_required", """
                SELECT is_nullable = 'YES'
                FROM information_schema.columns
                WHERE table_schema='public' AND table_name='ingestion_jobs' AND column_name='asset_id'
                """),
            ("ingestion_window_columns_missing", requireIngestionLedger ? """
                SELECT COUNT(*) = 25
                FROM information_schema.columns
                WHERE table_schema='public' AND table_name='ingestion_windows'
                  AND column_name IN (
                    'id','source','asset_id','job_type','range_start','range_end','contract_version',
                    'state','lease_owner','lease_token','lease_until','attempt_count','next_attempt_at',
                    'requested_calendar_count','expected_observation_count','raw_item_count',
                    'accepted_distinct_count','rejected_count','expected_no_data_count',
                    'outcome_code','error_code','calendar_release_id','created_at','updated_at','completed_at')
                """ : "SELECT TRUE"),
            ("ingestion_job_window_correlation_missing", requireIngestionLedger ? """
                SELECT COUNT(*) = 2
                FROM information_schema.columns
                WHERE table_schema='public' AND table_name='ingestion_jobs'
                  AND ((column_name='window_id' AND data_type='uuid' AND is_nullable='YES')
                    OR (column_name='outcome_code' AND character_maximum_length=80
                        AND is_nullable='YES'))
                """ : "SELECT TRUE"),
            ("activity_duration_not_bigint", """
                SELECT count(*)=1 AND bool_and(attribute.atttypid='bigint'::regtype)
                  FROM pg_catalog.pg_attribute attribute
                 WHERE attribute.attrelid='public.activity_logs'::regclass
                   AND attribute.attname='duration_ms'
                   AND attribute.attnum>0 AND NOT attribute.attisdropped
                """),
            ("activity_columns_mismatch", """
                SELECT COUNT(*) = 5
                  FROM pg_catalog.pg_attribute attribute
                 WHERE attribute.attrelid='public.activity_logs'::regclass
                   AND attribute.attnum>0 AND NOT attribute.attisdropped
                   AND ((attribute.attname='device_os' AND attribute.atttypmod=34)
                     OR (attribute.attname='os_version' AND attribute.atttypmod=104)
                     OR (attribute.attname='app_version' AND attribute.atttypmod=54)
                     OR (attribute.attname='country' AND attribute.atttypmod=6)
                     OR (attribute.attname='city' AND attribute.atttypmod=104))
                """),
            ("saved_scenario_columns_missing", """
                SELECT COUNT(*) = 4
                FROM information_schema.columns
                WHERE table_schema='public' AND table_name='saved_scenarios'
                  AND column_name IN ('type','extra_data','asset_symbol','asset_display_name')
                """),
            ("saved_scenario_nullability_mismatch", """
                SELECT COUNT(*) = 3
                FROM information_schema.columns
                WHERE table_schema='public' AND table_name='saved_scenarios'
                  AND ((column_name='asset_id' AND is_nullable='YES')
                    OR (column_name='asset_symbol' AND is_nullable='NO')
                    OR (column_name='asset_display_name' AND is_nullable='NO'))
                """),
            ("inflation_primary_key_mismatch", """
                SELECT COALESCE(array_agg(a.attname ORDER BY u.ordinality), ARRAY[]::name[]) = ARRAY['period_date','source']::name[]
                FROM pg_constraint c
                JOIN LATERAL unnest(c.conkey) WITH ORDINALITY u(attnum, ordinality) ON true
                JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = u.attnum
                WHERE c.conrelid = 'public.inflation_rates'::regclass AND c.contype = 'p'
                """),
            ("critical_constraints_missing", """
                SELECT COUNT(*) = __EXPECTED_CONSTRAINT_COUNT__ AND bool_and(convalidated)
                FROM pg_constraint
                WHERE connamespace='public'::regnamespace AND conname IN (
                    'chk_saved_scenarios_unit','chk_saved_scenarios_type','chk_users_tier',
                    'chk_activity_action','chk_ingestion_jobs_type','chk_ingestion_jobs_status',
                    'chk_inflation_rates_source','chk_activity_data_size','fk_price_points_asset',
                    'fk_ingestion_jobs_asset','fk_saved_scenarios_user','fk_saved_scenarios_asset',
                    'fk_market_holidays_asset')
                  AND (__REQUIRE_ACTION_CONSTRAINT__ OR conname<>'chk_activity_action')
                """.Replace("__EXPECTED_CONSTRAINT_COUNT__",
                    requireApiSecurityAdmission ? "12" : "13",
                    StringComparison.Ordinal).Replace("__REQUIRE_ACTION_CONSTRAINT__",
                    requireApiSecurityAdmission ? "FALSE" : "TRUE",
                    StringComparison.Ordinal)),
            ("phase2_indexes_missing", """
                SELECT COUNT(*) = 3
                FROM pg_indexes
                WHERE schemaname='public'
                  AND ((indexname='uq_users_device_id' AND indexdef LIKE '%WHERE (device_id IS NOT NULL)%')
                    OR (indexname='uq_users_email' AND indexdef LIKE '%WHERE (email IS NOT NULL)%')
                    OR (indexname='idx_activity_logs_data_gin' AND indexdef LIKE '%USING gin%'))
                """),
            ("ingestion_window_constraints_missing", requireIngestionLedger ? """
                SELECT COUNT(*) = 14 AND bool_and(convalidated)
                FROM pg_constraint
                WHERE connamespace='public'::regnamespace AND conname IN (
                    'fk_ingestion_windows_asset','uq_ingestion_windows_logical',
                    'chk_ingestion_windows_range','chk_ingestion_windows_contract',
                    'chk_ingestion_windows_attempt','chk_ingestion_windows_counts',
                    'chk_ingestion_windows_terminal_completeness','chk_ingestion_windows_state',
                    'chk_ingestion_windows_lease','chk_ingestion_windows_completed',
                    'chk_ingestion_windows_outcome_codes','chk_ingestion_windows_error_codes',
                    'fk_ingestion_jobs_window','fk_ingestion_windows_calendar_release')
                """ : "SELECT TRUE"),
            ("ingestion_window_nullsafe_unique_missing", requireIngestionLedger ? """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_index i
                    JOIN pg_class idx ON idx.oid=i.indexrelid
                    WHERE idx.relnamespace='public'::regnamespace
                      AND idx.relname='uq_ingestion_windows_logical'
                      AND i.indisunique AND i.indnullsnotdistinct)
                """ : "SELECT TRUE"),
            ("ingestion_write_fence_missing", requireIngestionWriteFence ? """
                SELECT COUNT(*) = 2 AND bool_and((
                    (t.tgrelid='public.price_points'::regclass
                        AND t.tgname='trg_price_points_ingestion_fence'
                        AND t.tgenabled='O'
                        AND p.proname='enforce_price_point_ingestion_fence'
                        AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')=
                            '4e64afe06288d5700543dd7565505935b7ab74e5a102b00f6d9c56ed4290a416')
                    OR
                    (t.tgrelid='public.inflation_rates'::regclass
                        AND t.tgname='trg_inflation_rates_ingestion_fence'
                        AND t.tgenabled='A'
                        AND p.proname='enforce_inflation_rate_ingestion_fence'
                        AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')=
                            'ceae4a377df47e9a268e0e37f347c8ef17f56afcb819c3d8a762852530fbffaa'))
                    AND t.tgtype=23
                    AND pn.nspname='public'
                    AND p.prorettype='trigger'::regtype AND p.pronargs=0
                    AND l.lanname='plpgsql' AND NOT p.prosecdef AND p.provolatile='v'
                    AND encode(sha256(convert_to(
                        coalesce(array_to_string(p.proconfig,E'\n'),''),'UTF8')),'hex')=
                        '__WRITE_FENCE_CONFIG_SHA256__')
                FROM pg_trigger t
                JOIN pg_proc p ON p.oid=t.tgfoid
                JOIN pg_namespace pn ON pn.oid=p.pronamespace
                JOIN pg_language l ON l.oid=p.prolang
                WHERE NOT t.tgisinternal
                  AND ((t.tgrelid='public.price_points'::regclass
                        AND t.tgname='trg_price_points_ingestion_fence')
                    OR (t.tgrelid='public.inflation_rates'::regclass
                        AND t.tgname='trg_inflation_rates_ingestion_fence'))
                """.Replace("__WRITE_FENCE_CONFIG_SHA256__",
                    requirePrivilegeSeparation
                        ? "20bd6867b3d59c73bbacde2d9a7e7acd1be5b3c154993be84f528ba9d185bd6d"
                        : "7b14df390c9fb0194589823f48b5dde100196e6260dd2c60c96a0c147d944c24",
                    StringComparison.Ordinal) : "SELECT TRUE"),
            ("authoritative_market_calendars_missing", requireAuthoritativeCalendars ? """
                SELECT COUNT(*) = 6
                  FROM information_schema.tables
                 WHERE table_schema='public' AND table_name IN (
                    'market_calendars','market_calendar_releases',
                    'market_calendar_release_sources','market_calendar_days',
                    'market_calendar_active_releases','asset_market_calendars')
                """ : "SELECT TRUE"),
            ("authoritative_market_calendar_payload_mismatch", requireAuthoritativeCalendars ? """
                SELECT COUNT(*) = 2 AND bool_and(
                       (id='ca100000-0000-7000-8000-000000000001'
                        AND calendar_code='tcmb_indicative_fx' AND coverage_from='2006-01-01'
                        AND coverage_through='2026-08-17' AND row_count=7534
                        AND normalized_sha256='de8f0ff7654ae4972d081f1d2a225de6997986cd8297736715b3e71bfda1b1da'
                        AND source_bundle_sha256='e95b9889c8857ca4e5ae5804704795265655deb30cd15f3224882a9f419feec9'
                        AND sealed_at IS NOT NULL)
                       OR
                       (id='ca100000-0000-7000-8000-000000000002'
                        AND calendar_code='bist_pay_xist' AND coverage_from='2024-01-01'
                        AND coverage_through='2026-12-31' AND row_count=1096
                        AND normalized_sha256='82c463fec5abf9663b689d863da9e7efcd93b976747e869c5aaeccfe7a4feed0'
                        AND source_bundle_sha256='a93a6905c5213cdd30ad5a4ab4b9bcdb0698cf943c890aa44f462a1f3323c9b3'
                        AND sealed_at IS NOT NULL))
                  FROM market_calendar_releases
                 WHERE id IN ('ca100000-0000-7000-8000-000000000001',
                              'ca100000-0000-7000-8000-000000000002')
                """ : "SELECT TRUE"),
            ("authoritative_market_calendar_rows_mismatch", requireAuthoritativeCalendars ? """
                SELECT (SELECT count(*) FROM market_calendar_days
                         WHERE release_id='ca100000-0000-7000-8000-000000000001') = 7534
                   AND (SELECT count(*) FROM market_calendar_days
                         WHERE release_id='ca100000-0000-7000-8000-000000000002') = 1096
                   AND (SELECT count(*) FROM market_calendar_release_sources
                         WHERE release_id='ca100000-0000-7000-8000-000000000001') = 270
                   AND (SELECT count(*) FROM market_calendar_release_sources
                         WHERE release_id='ca100000-0000-7000-8000-000000000002') = 4
                """ : "SELECT TRUE"),
            ("authoritative_market_calendar_release_invalid", requireAuthoritativeCalendars ? """
                SELECT COUNT(*) >= 2
                   AND bool_and(sealed_at IS NOT NULL)
                   AND bool_and(verify_market_calendar_release_payload(id))
                  FROM market_calendar_releases
                """ : "SELECT TRUE"),
            ("authoritative_market_calendar_active_set_invalid", requireAuthoritativeCalendars ? """
                SELECT (SELECT count(*) FROM market_calendars) = 2
                   AND COUNT(*) = 2
                   AND bool_and(active.calendar_code IN ('tcmb_indicative_fx','bist_pay_xist'))
                   AND bool_and(release.sealed_at IS NOT NULL)
                  FROM market_calendar_active_releases active
                  JOIN market_calendar_releases release
                    ON release.id=active.release_id
                   AND release.calendar_code=active.calendar_code
                """ : "SELECT TRUE"),
            ("authoritative_market_calendar_guards_missing", requireAuthoritativeCalendars ? """
                WITH expected(name,relation_name,function_name,trigger_type) AS (VALUES
                    ('trg_market_calendar_releases_immutable','market_calendar_releases','enforce_market_calendar_release_assembly',27),
                    ('trg_market_calendar_releases_no_truncate','market_calendar_releases','enforce_market_calendar_release_assembly',34),
                    ('trg_market_calendar_release_sources_immutable','market_calendar_release_sources','enforce_market_calendar_release_assembly',31),
                    ('trg_market_calendar_release_sources_no_truncate','market_calendar_release_sources','enforce_market_calendar_release_assembly',34),
                    ('trg_market_calendar_days_immutable','market_calendar_days','enforce_market_calendar_release_assembly',31),
                    ('trg_market_calendar_days_no_truncate','market_calendar_days','enforce_market_calendar_release_assembly',34),
                    ('trg_market_calendar_active_release_sealed','market_calendar_active_releases','enforce_active_market_calendar_release',31),
                    ('trg_market_calendar_active_releases_no_truncate','market_calendar_active_releases','enforce_active_market_calendar_release',34),
                    ('trg_asset_market_calendar_source','asset_market_calendars','enforce_asset_market_calendar_source',31),
                    ('trg_asset_market_calendars_no_truncate','asset_market_calendars','enforce_asset_market_calendar_source',34),
                    ('trg_assets_calendar_source_immutable','assets','provision_asset_market_calendar',19),
                    ('trg_assets_calendar_provision','assets','provision_asset_market_calendar',5),
                    ('trg_ingestion_window_calendar_release','ingestion_windows','enforce_ingestion_window_calendar_release',23)
                )
                SELECT count(*)=13 AND count(t.oid)=13 AND bool_and(
                           t.tgenabled='O' AND t.tgtype=expected.trigger_type
                           AND p.proname=expected.function_name
                           AND pn.nspname='public')
                  FROM expected
                  LEFT JOIN pg_trigger t ON t.tgname=expected.name
                    AND t.tgrelid=('public.' || expected.relation_name)::regclass
                    AND NOT t.tgisinternal
                  LEFT JOIN pg_proc p ON p.oid=t.tgfoid
                  LEFT JOIN pg_namespace pn ON pn.oid=p.pronamespace
                """ : "SELECT TRUE"),
            ("authoritative_market_calendar_function_security_invalid",
                requireAuthoritativeCalendars && !requirePrivilegeSeparation ? """
                WITH expected(name,body_sha256) AS (VALUES
                    ('activate_market_calendar_release','9c7680d37e98ae75475ccac4afc96798f958cf0ea897c7f2ded6fdb19879c9c9'),
                    ('enforce_active_market_calendar_release','5bade313804c0e597b9af28a6edb1143600eaefd5345cb347fe9daedd5f4ee6f'),
                    ('enforce_asset_market_calendar_source','32086cca3e0712a9fac701f54b7801ef43a1bfb2574d1fdcb03485680cbbe2f4'),
                    ('enforce_ingestion_window_calendar_release','ae2468290e4f09338e9120f25bafa65e3575b1f6dc941aa65e4867f733de428a'),
                    ('enforce_market_calendar_release_assembly','7a00eb9a7c0a8e266ff7f10543efa757924dec410fe573eac728a38adbe679db'),
                    ('provision_asset_market_calendar','32c07aca82ae50c6b3638015df2792bd421a4aa3e09f85aea9089dd0b3ec7392'),
                    ('seal_market_calendar_release','d41d5fb586594ec56de83c6a85e71108a292e8d1adb23863dae4b9de137a3e4c'),
                    ('verify_market_calendar_release_payload','1efd84a21cb751b2a4254bd5440feaf716e7bd69390f7aeb9c02ba45665d84dd')
                )
                SELECT count(*)=8 AND count(p.oid)=8 AND bool_and(
                           NOT p.prosecdef
                           AND p.proconfig=ARRAY['search_path=pg_catalog, public, pg_temp']::text[]
                           AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')=expected.body_sha256
                           AND (p.proname NOT IN ('seal_market_calendar_release','activate_market_calendar_release')
                                OR NOT EXISTS (
                                    SELECT 1
                                      FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) acl
                                     WHERE acl.grantee=0 AND acl.privilege_type='EXECUTE')))
                  FROM expected
                  LEFT JOIN pg_proc p ON p.proname=expected.name
                  LEFT JOIN pg_namespace n ON n.oid=p.pronamespace AND n.nspname='public'
                 WHERE n.oid IS NOT NULL
                """ : "SELECT TRUE"),
            ("scenario_integrity_constraints_missing", requireScenarioIntegrity ? """
                SELECT COUNT(*) = 3 AND bool_and(convalidated)
                  FROM pg_constraint
                 WHERE conrelid='public.saved_scenarios'::regclass
                   AND conname IN ('chk_saved_scenarios_extra_data_object',
                                   'chk_saved_scenarios_extra_data_size',
                                   'chk_saved_scenarios_type_unit')
                """ : "SELECT TRUE"),
            ("scenario_keyset_index_missing", requireScenarioIntegrity ? """
                SELECT COUNT(*) = 1
                   AND bool_and(i.indisvalid AND i.indisready AND NOT i.indisunique
                                AND i.indpred IS NULL AND i.indexprs IS NULL
                                AND i.indrelid='public.saved_scenarios'::regclass
                                AND am.amname='btree')
                   AND NOT EXISTS (
                       SELECT 1
                         FROM pg_class legacy
                        WHERE legacy.relnamespace='public'::regnamespace
                          AND legacy.relname='idx_saved_scenarios_user')
                  FROM pg_index i
                  JOIN pg_class idx ON idx.oid=i.indexrelid
                  JOIN pg_am am ON am.oid=idx.relam
                 WHERE idx.relnamespace='public'::regnamespace
                   AND idx.relname='idx_saved_scenarios_user_created_id_desc'
                   AND pg_get_indexdef(i.indexrelid)
                       LIKE '%(user_id, created_at DESC, id DESC)%'
                """ : "SELECT TRUE"),
            ("scenario_hard_cap_guard_missing", requireScenarioIntegrity ? """
                SELECT COUNT(*) = 1 AND bool_and(
                           t.tgenabled='O'
                           AND t.tgtype=7
                           AND p.proname='enforce_saved_scenario_hard_cap'
                           AND pn.nspname='public'
                           AND NOT p.prosecdef
                           AND p.proconfig=ARRAY['__HARD_CAP_SEARCH_PATH__']::text[]
                           AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')
                               = 'c4bd2c3b9f61faa2394bd9d5eec0075043ba8e16680a8b6e7882d9f37854c42c'
                           AND NOT EXISTS (
                               SELECT 1
                                 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) acl
                                WHERE acl.grantee=0 AND acl.privilege_type='EXECUTE'))
                  FROM pg_trigger t
                  JOIN pg_proc p ON p.oid=t.tgfoid
                  JOIN pg_namespace pn ON pn.oid=p.pronamespace
                 WHERE t.tgrelid='public.saved_scenarios'::regclass
                   AND t.tgname='trg_saved_scenarios_hard_cap'
                   AND NOT t.tgisinternal
                """.Replace("__HARD_CAP_SEARCH_PATH__",
                    requirePrivilegeSeparation
                        ? "search_path=pg_catalog, pg_temp"
                        : "search_path=pg_catalog, public, pg_temp",
                    StringComparison.Ordinal) : "SELECT TRUE"),
        };

        foreach (var (code, sql) in checks)
        {
            bool passed;
            try
            {
                passed = await ScalarAsync<bool>(connection, sql, cancellationToken);
            }
            catch (MigratorRejectedException exception) when (exception.Code == "database_scalar_missing")
            {
                throw new MigratorRejectedException("schema_fingerprint_mismatch", code, exception);
            }
            if (!passed)
                throw new MigratorRejectedException("schema_fingerprint_mismatch", code);
        }
        if (requirePrivilegeSeparation)
            await VerifyPrivilegeSeparationAsync(
                connection, requirePriceAuthority, requireApiTrust,
                requireCredentialRehash, cancellationToken);
        if (requirePriceAuthority)
            await VerifyPriceAuthorityAsync(connection, cancellationToken);
        if (requireApiTrust)
            await VerifyApiTrustAsync(connection, requireCredentialRehash, cancellationToken);
        if (requirePrincipalRetention)
            await VerifyPrincipalRetentionAsync(
                connection, requireApiSecurityAdmission, cancellationToken);
        if (requireApiSecurityAdmission)
            await VerifyApiSecurityAdmissionAsync(connection, cancellationToken);
    }

    private async Task VerifyPrivilegeSeparationAsync(
        NpgsqlConnection connection,
        bool requirePriceAuthority,
        bool requireApiTrust,
        bool requireCredentialRehash,
        CancellationToken cancellationToken)
    {
        await AssertSecurityFingerprintAsync(connection, "role_contract_singleton_mismatch", """
            SELECT count(*)=1 AND bool_and(
                       contract_schema_version=1 AND contract_sha256=$1 AND deployment_id=$2
                       AND database_name=current_database() AND system_identifier_sha256=$3
                       AND role_prefix=$4 AND owner_role=$5 AND migrator_capability_role=$6
                       AND api_capability_role=$7 AND ingestion_capability_role=$8
                       AND calendar_importer_capability_role=$9 AND exporter_capability_role=$10
                       AND audit_capability_role=$11 AND timescale_scheduler_role=$12)
              FROM public.saydin_role_contract
            """, cancellationToken,
            options.ContractSha256, options.Contract.DeploymentId,
            options.Contract.SystemIdentifierSha256, options.Contract.Prefix,
            options.Contract.Owner.Name, options.Contract.MigratorCapability.Name,
            options.Contract.ApiCapability.Name, options.Contract.IngestionCapability.Name,
            options.Contract.CalendarImporterCapability.Name, options.Contract.ExporterCapability.Name,
            options.Contract.AuditCapability.Name, options.Contract.TimescaleScheduler.Name);

        await AssertSecurityFingerprintAsync(connection, "application_relation_security_mismatch", """
            WITH base(name) AS (VALUES
                ('activity_logs'),('asset_market_calendars'),('assets'),('inflation_rates'),
                ('ingestion_jobs'),('ingestion_windows'),('market_calendar_active_releases'),
                ('market_calendar_days'),('market_calendar_release_sources'),
                ('market_calendar_releases'),('market_calendars'),('market_holidays'),
                ('price_points'),('saved_scenarios'),('schema_migrations'),
                ('saydin_migration_control'),('saydin_role_contract'),('users')),
            authority(name) AS (VALUES
                ('provider_fetch_payloads'),('price_observation_attributions'),
                ('inflation_observation_attributions')),
            api_trust(name) AS (VALUES
                ('installation_credentials'),('asset_catalog_state')),
            expected(name) AS (
                SELECT name FROM base
                UNION ALL SELECT name FROM authority WHERE $3
                UNION ALL SELECT name FROM api_trust WHERE $4)
            SELECT count(*)=18 + CASE WHEN $3 THEN 3 ELSE 0 END
                                 + CASE WHEN $4 THEN 2 ELSE 0 END
               AND count(relation.oid)=18 + CASE WHEN $3 THEN 3 ELSE 0 END
                                             + CASE WHEN $4 THEN 2 ELSE 0 END
               AND bool_and(
                       relation.relkind IN ('r','p') AND relation.relpersistence='p'
                       AND pg_catalog.pg_get_userbyid(relation.relowner)=
                           CASE WHEN expected.name='activity_logs' THEN $2 ELSE $1 END
                       AND NOT relation.relrowsecurity AND NOT relation.relforcerowsecurity
                       AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy
                                       WHERE policy.polrelid=relation.oid))
              FROM expected
              LEFT JOIN pg_catalog.pg_class relation
                ON relation.relnamespace='public'::pg_catalog.regnamespace
               AND relation.relname=expected.name
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.TimescaleScheduler.Name, requirePriceAuthority, requireApiTrust);

        await AssertSecurityFingerprintAsync(connection, "application_table_acl_mismatch", """
            WITH base_relations(name) AS (VALUES
                ('activity_logs'),('asset_market_calendars'),('assets'),('inflation_rates'),
                ('ingestion_jobs'),('ingestion_windows'),('market_calendar_active_releases'),
                ('market_calendar_days'),('market_calendar_release_sources'),
                ('market_calendar_releases'),('market_calendars'),('market_holidays'),
                ('price_points'),('saved_scenarios'),('schema_migrations'),
                ('saydin_migration_control'),('saydin_role_contract'),('users')),
            authority_relations(name) AS (VALUES
                ('provider_fetch_payloads'),('price_observation_attributions'),
                ('inflation_observation_attributions')),
            api_trust_relations(name) AS (VALUES
                ('installation_credentials'),('asset_catalog_state')),
            relations(name) AS (
                SELECT name FROM base_relations
                UNION ALL SELECT name FROM authority_relations WHERE $7
                UNION ALL SELECT name FROM api_trust_relations WHERE $8),
            base_grants(name,grantee,privilege_type) AS (VALUES
                ('activity_logs',$3,'INSERT'),
                ('assets',$3,'SELECT'),('price_points',$3,'SELECT'),
                ('inflation_rates',$3,'SELECT'),('users',$3,'SELECT'),
                ('saved_scenarios',$3,'SELECT'),('saved_scenarios',$3,'INSERT'),
                ('saved_scenarios',$3,'DELETE'),
                ('assets',$4,'SELECT'),('price_points',$4,'SELECT'),
                ('inflation_rates',$4,'SELECT'),('ingestion_windows',$4,'SELECT'),
                ('ingestion_jobs',$4,'SELECT'),('market_calendar_releases',$4,'SELECT'),
                ('market_calendar_days',$4,'SELECT'),
                ('market_calendar_active_releases',$4,'SELECT'),
                ('asset_market_calendars',$4,'SELECT'),('market_holidays',$4,'SELECT'),
                ('ingestion_windows',$4,'INSERT'),('ingestion_windows',$4,'UPDATE'),
                ('ingestion_jobs',$4,'INSERT'),('ingestion_jobs',$4,'UPDATE'),
                ('market_calendar_releases',$5,'SELECT'),
                ('market_calendar_releases',$5,'INSERT'),
                ('market_calendar_release_sources',$5,'SELECT'),
                ('market_calendar_release_sources',$5,'INSERT'),
                ('market_calendar_days',$5,'INSERT'),
                ('asset_market_calendars',$6,'SELECT'),('assets',$6,'SELECT'),
                ('inflation_rates',$6,'SELECT'),('ingestion_jobs',$6,'SELECT'),
                ('ingestion_windows',$6,'SELECT'),
                ('market_calendar_active_releases',$6,'SELECT'),
                ('market_calendar_days',$6,'SELECT'),
                ('market_calendar_release_sources',$6,'SELECT'),
                ('market_calendar_releases',$6,'SELECT'),
                ('market_calendars',$6,'SELECT'),('price_points',$6,'SELECT'),
                ('schema_migrations',$6,'SELECT'),('saydin_migration_control',$6,'SELECT'),
                ('saydin_role_contract',$6,'SELECT')),
            legacy_grants(name,grantee,privilege_type) AS (VALUES
                ('price_points',$4,'INSERT'),('price_points',$4,'UPDATE'),
                ('inflation_rates',$4,'INSERT'),('inflation_rates',$4,'UPDATE')),
            authority_grants(name,grantee,privilege_type) AS (VALUES
                ('provider_fetch_payloads',$4,'SELECT'),
                ('price_observation_attributions',$4,'SELECT'),
                ('inflation_observation_attributions',$4,'SELECT'),
                ('provider_fetch_payloads',$6,'SELECT'),
                ('price_observation_attributions',$6,'SELECT'),
                ('inflation_observation_attributions',$6,'SELECT')),
            api_trust_grants(name,grantee,privilege_type) AS (VALUES
                ('asset_catalog_state',$6,'SELECT')),
            grants AS (
                SELECT * FROM base_grants
                UNION ALL SELECT * FROM legacy_grants WHERE NOT $7
                UNION ALL SELECT * FROM authority_grants WHERE $7
                UNION ALL SELECT * FROM api_trust_grants WHERE $8),
            expected AS (
                SELECT name,grantee,CASE WHEN name='activity_logs' THEN $2 ELSE $1 END AS grantor,
                       privilege_type,false AS is_grantable FROM grants),
            actual AS (
                SELECT relation.relname,grantee.rolname,grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM relations
                  JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace='public'::pg_catalog.regnamespace
                   AND relation.relname=relations.name
                  CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(relation.relacl,
                      pg_catalog.acldefault('r',relation.relowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE acl.grantee<>relation.relowner),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.TimescaleScheduler.Name, options.Contract.ApiCapability.Name,
            options.Contract.IngestionCapability.Name,
            options.Contract.CalendarImporterCapability.Name,
            options.Contract.AuditCapability.Name, requirePriceAuthority, requireApiTrust);

        await AssertSecurityFingerprintAsync(connection, "application_column_acl_mismatch", """
            WITH user_expected(relation_name,column_name,grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ('users','id',$2,$1,'INSERT',false),
                ('users','device_id',$2,$1,'INSERT',false),
                ('users','tier',$2,$1,'INSERT',false),
                ('users','created_at',$2,$1,'INSERT',false),
                ('users','last_seen_at',$2,$1,'INSERT',false),
                ('users','last_seen_at',$2,$1,'UPDATE',false)),
            authority_expected(relation_name,column_name,grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ('price_points','asset_id',$3,$1,'INSERT',false),
                ('price_points','price_date',$3,$1,'INSERT',false),
                ('price_points','close',$3,$1,'INSERT',false),('price_points','close',$3,$1,'UPDATE',false),
                ('price_points','open',$3,$1,'INSERT',false),('price_points','open',$3,$1,'UPDATE',false),
                ('price_points','high',$3,$1,'INSERT',false),('price_points','high',$3,$1,'UPDATE',false),
                ('price_points','low',$3,$1,'INSERT',false),('price_points','low',$3,$1,'UPDATE',false),
                ('price_points','volume',$3,$1,'INSERT',false),('price_points','volume',$3,$1,'UPDATE',false),
                ('price_points','provider_source',$3,$1,'INSERT',false),('price_points','provider_source',$3,$1,'UPDATE',false),
                ('price_points','source_observation_id',$3,$1,'INSERT',false),('price_points','source_observation_id',$3,$1,'UPDATE',false),
                ('price_points','as_of_at',$3,$1,'INSERT',false),('price_points','as_of_at',$3,$1,'UPDATE',false),
                ('price_points','price_kind',$3,$1,'INSERT',false),('price_points','price_kind',$3,$1,'UPDATE',false),
                ('price_points','is_final',$3,$1,'INSERT',false),('price_points','is_final',$3,$1,'UPDATE',false),
                ('price_points','observation_sha256',$3,$1,'INSERT',false),('price_points','observation_sha256',$3,$1,'UPDATE',false),
                ('price_points','authority_contract_version',$3,$1,'INSERT',false),('price_points','authority_contract_version',$3,$1,'UPDATE',false),
                ('price_points','source_raw',$3,$1,'INSERT',false),('price_points','source_raw',$3,$1,'UPDATE',false),
                ('inflation_rates','period_date',$3,$1,'INSERT',false),
                ('inflation_rates','index_value',$3,$1,'INSERT',false),('inflation_rates','index_value',$3,$1,'UPDATE',false),
                ('inflation_rates','source',$3,$1,'INSERT',false),
                ('inflation_rates','provider_source',$3,$1,'INSERT',false),('inflation_rates','provider_source',$3,$1,'UPDATE',false),
                ('inflation_rates','source_observation_id',$3,$1,'INSERT',false),('inflation_rates','source_observation_id',$3,$1,'UPDATE',false),
                ('inflation_rates','as_of_at',$3,$1,'INSERT',false),('inflation_rates','as_of_at',$3,$1,'UPDATE',false),
                ('inflation_rates','price_kind',$3,$1,'INSERT',false),('inflation_rates','price_kind',$3,$1,'UPDATE',false),
                ('inflation_rates','is_final',$3,$1,'INSERT',false),('inflation_rates','is_final',$3,$1,'UPDATE',false),
                ('inflation_rates','observation_sha256',$3,$1,'INSERT',false),('inflation_rates','observation_sha256',$3,$1,'UPDATE',false),
                ('inflation_rates','authority_contract_version',$3,$1,'INSERT',false),('inflation_rates','authority_contract_version',$3,$1,'UPDATE',false),
                ('inflation_rates','source_raw',$3,$1,'INSERT',false),('inflation_rates','source_raw',$3,$1,'UPDATE',false),
                ('provider_fetch_payloads','provider_source',$3,$1,'INSERT',false),
                ('provider_fetch_payloads','payload_sha256',$3,$1,'INSERT',false),
                ('provider_fetch_payloads','payload_byte_length',$3,$1,'INSERT',false),
                ('price_observation_attributions','asset_id',$3,$1,'INSERT',false),
                ('price_observation_attributions','price_date',$3,$1,'INSERT',false),
                ('price_observation_attributions','ingestion_window_id',$3,$1,'INSERT',false),
                ('price_observation_attributions','provider_source',$3,$1,'INSERT',false),
                ('price_observation_attributions','payload_sha256',$3,$1,'INSERT',false),
                ('price_observation_attributions','source_observation_id',$3,$1,'INSERT',false),
                ('price_observation_attributions','observation_sha256',$3,$1,'INSERT',false),
                ('price_observation_attributions','authority_contract_version',$3,$1,'INSERT',false),
                ('inflation_observation_attributions','period_date',$3,$1,'INSERT',false),
                ('inflation_observation_attributions','source',$3,$1,'INSERT',false),
                ('inflation_observation_attributions','ingestion_window_id',$3,$1,'INSERT',false),
                ('inflation_observation_attributions','provider_source',$3,$1,'INSERT',false),
                ('inflation_observation_attributions','payload_sha256',$3,$1,'INSERT',false),
                ('inflation_observation_attributions','source_observation_id',$3,$1,'INSERT',false),
                ('inflation_observation_attributions','observation_sha256',$3,$1,'INSERT',false),
                ('inflation_observation_attributions','authority_contract_version',$3,$1,'INSERT',false)),
            expected AS (
                SELECT * FROM user_expected
                UNION ALL SELECT * FROM authority_expected WHERE $4),
            actual AS (
                SELECT relation.relname AS relation_name,attribute.attname AS column_name,
                       grantee.rolname AS grantee,grantor.rolname AS grantor,
                       acl.privilege_type,acl.is_grantable
                  FROM pg_catalog.pg_attribute attribute
                  JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
                  CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE relation.relnamespace='public'::pg_catalog.regnamespace
                   AND attribute.attnum>0 AND NOT attribute.attisdropped
                   AND acl.grantee<>relation.relowner),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.ApiCapability.Name, options.Contract.IngestionCapability.Name,
            requirePriceAuthority);

        await AssertSecurityFingerprintAsync(connection, "asset_category_acl_mismatch", """
            WITH expected(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ($2,$1,'USAGE',false),($3,$1,'USAGE',false)),
            actual AS (
                SELECT grantee.rolname,grantor.rolname,acl.privilege_type,acl.is_grantable
                  FROM pg_catalog.pg_type type
                  CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(type.typacl,
                      pg_catalog.acldefault('T',type.typowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE type.oid='public.asset_category'::pg_catalog.regtype
                   AND acl.grantee<>type.typowner),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.ApiCapability.Name, options.Contract.IngestionCapability.Name);

        await AssertSecurityFingerprintAsync(connection, "application_function_acl_mismatch", """
            WITH base_functions(signature) AS (VALUES
                ('activate_market_calendar_release(text,uuid,uuid)'),
                ('enforce_active_market_calendar_release()'),
                ('enforce_asset_market_calendar_source()'),
                ('enforce_inflation_rate_ingestion_fence()'),
                ('enforce_ingestion_window_calendar_release()'),
                ('enforce_market_calendar_release_assembly()'),
                ('enforce_price_point_ingestion_fence()'),
                ('enforce_saved_scenario_hard_cap()'),
                ('provision_asset_market_calendar()'),('seal_market_calendar_release(uuid)'),
                ('verify_market_calendar_release_payload(uuid)')),
            authority_functions(signature) AS (VALUES
                ('saydin_source_raw_allowed(jsonb)'),('saydin_canonical_observation(jsonb)'),
                ('enforce_price_point_authority()'),('enforce_inflation_rate_authority()'),
                ('enforce_observation_attribution()'),('enforce_fetch_payload_insert()'),
                ('reject_fetch_payload_mutation()')),
            api_trust_functions(signature) AS (VALUES
                ('compute_asset_catalog_sha256()'),('refresh_asset_catalog_state()'),
                ('register_installation(uuid,uuid,bytea,smallint)'),
                ('resolve_installation(bytea,smallint)'),
                ('begin_installation_rotation(bytea,smallint,uuid,uuid,bytea,smallint)'),
                ('commit_installation_rotation(uuid,bytea,smallint)'),
                ('revoke_installation(bytea,smallint)'),('get_asset_catalog_state()')),
            credential_rehash_functions(signature) AS (VALUES
                ('resolve_installation_and_rehash(bytea,smallint,bytea,smallint)')),
            functions(signature) AS (
                SELECT signature FROM base_functions
                UNION ALL SELECT signature FROM authority_functions WHERE $5
                UNION ALL SELECT signature FROM api_trust_functions WHERE $6
                UNION ALL SELECT signature FROM credential_rehash_functions WHERE $8),
            base_expected(signature,grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ('activate_market_calendar_release(text,uuid,uuid)',$2,$1,'EXECUTE',false),
                ('seal_market_calendar_release(uuid)',$2,$1,'EXECUTE',false),
                ('verify_market_calendar_release_payload(uuid)',$3,$1,'EXECUTE',false)),
            authority_expected(signature,grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ('saydin_source_raw_allowed(jsonb)',$4,$1,'EXECUTE',false),
                ('saydin_canonical_observation(jsonb)',$4,$1,'EXECUTE',false)),
            api_trust_expected(signature,grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ('register_installation(uuid,uuid,bytea,smallint)',$7,$1,'EXECUTE',false),
                ('resolve_installation(bytea,smallint)',$7,$1,'EXECUTE',false),
                ('begin_installation_rotation(bytea,smallint,uuid,uuid,bytea,smallint)',$7,$1,'EXECUTE',false),
                ('commit_installation_rotation(uuid,bytea,smallint)',$7,$1,'EXECUTE',false),
                ('revoke_installation(bytea,smallint)',$7,$1,'EXECUTE',false),
                ('get_asset_catalog_state()',$7,$1,'EXECUTE',false)),
            credential_rehash_expected(
                signature,grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ('resolve_installation_and_rehash(bytea,smallint,bytea,smallint)',
                 $7,$1,'EXECUTE',false)),
            expected AS (
                SELECT * FROM base_expected
                UNION ALL SELECT * FROM authority_expected WHERE $5
                UNION ALL SELECT * FROM api_trust_expected WHERE $6
                UNION ALL SELECT * FROM credential_rehash_expected WHERE $8),
            actual AS (
                SELECT functions.signature,grantee.rolname,grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM functions
                  JOIN pg_catalog.pg_proc function
                    ON function.oid=pg_catalog.to_regprocedure('public.'||functions.signature)
                  CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(function.proacl,
                      pg_catalog.acldefault('f',function.proowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE acl.grantee<>function.proowner),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.CalendarImporterCapability.Name, options.Contract.AuditCapability.Name,
            options.Contract.IngestionCapability.Name, requirePriceAuthority, requireApiTrust,
            options.Contract.ApiCapability.Name, requireCredentialRehash);

        await AssertSecurityFingerprintAsync(connection, "owner_default_acl_mismatch", """
            WITH defaults AS (
                SELECT defaults.*
                  FROM pg_catalog.pg_default_acl defaults
                 WHERE defaults.defaclrole=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=$1))
            SELECT count(*)=2 AND count(DISTINCT defaclobjtype)=2
               AND bool_and(defaclnamespace=0 AND defaclobjtype IN ('f','T'))
               AND NOT EXISTS (
                   SELECT 1 FROM defaults
                   CROSS JOIN LATERAL pg_catalog.aclexplode(defaults.defaclacl) acl
                   WHERE acl.grantee<>defaults.defaclrole OR acl.grantor<>defaults.defaclrole
                      OR acl.is_grantable
                      OR (defaults.defaclobjtype='f' AND acl.privilege_type<>'EXECUTE')
                      OR (defaults.defaclobjtype='T' AND acl.privilege_type<>'USAGE'))
              FROM defaults
            """, cancellationToken, options.Contract.Owner.Name);

        await AssertSecurityFingerprintAsync(connection, "calendar_function_security_mismatch", """
            WITH expected(name,body_sha256,security_definer,direct_grantee) AS (VALUES
                ('activate_market_calendar_release','9c7680d37e98ae75475ccac4afc96798f958cf0ea897c7f2ded6fdb19879c9c9',true,$2),
                ('enforce_active_market_calendar_release','5bade313804c0e597b9af28a6edb1143600eaefd5345cb347fe9daedd5f4ee6f',false,NULL),
                ('enforce_asset_market_calendar_source','32086cca3e0712a9fac701f54b7801ef43a1bfb2574d1fdcb03485680cbbe2f4',false,NULL),
                ('enforce_ingestion_window_calendar_release','ae2468290e4f09338e9120f25bafa65e3575b1f6dc941aa65e4867f733de428a',false,NULL),
                ('enforce_market_calendar_release_assembly','7a00eb9a7c0a8e266ff7f10543efa757924dec410fe573eac728a38adbe679db',true,NULL),
                ('provision_asset_market_calendar','32c07aca82ae50c6b3638015df2792bd421a4aa3e09f85aea9089dd0b3ec7392',false,NULL),
                ('seal_market_calendar_release','d41d5fb586594ec56de83c6a85e71108a292e8d1adb23863dae4b9de137a3e4c',true,$2),
                ('verify_market_calendar_release_payload','1efd84a21cb751b2a4254bd5440feaf716e7bd69390f7aeb9c02ba45665d84dd',false,$3))
            SELECT count(*)=8 AND count(function.oid)=8 AND bool_and(
                       pg_catalog.pg_get_userbyid(function.proowner)=$1
                       AND function.prosecdef=expected.security_definer
                       AND function.proconfig=ARRAY['search_path=pg_catalog, pg_temp']::text[]
                       AND pg_catalog.encode(pg_catalog.sha256(
                           pg_catalog.convert_to(function.prosrc,'UTF8')),'hex')=expected.body_sha256
                       AND NOT EXISTS (
                           SELECT 1 FROM pg_catalog.aclexplode(
                               coalesce(function.proacl,
                                   pg_catalog.acldefault('f',function.proowner))) acl
                           LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                           LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                           WHERE acl.grantee<>function.proowner
                             AND (expected.direct_grantee IS NULL
                                  OR grantee.rolname<>expected.direct_grantee
                                  OR acl.privilege_type<>'EXECUTE' OR acl.is_grantable
                                  OR grantor.rolname<>$1)))
              FROM expected
              LEFT JOIN pg_catalog.pg_proc function ON function.proname=expected.name
               AND function.pronamespace='public'::pg_catalog.regnamespace
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.CalendarImporterCapability.Name, options.Contract.AuditCapability.Name);

        await AssertSecurityFingerprintAsync(connection, "write_fence_function_security_mismatch", """
            SELECT count(*)=3 AND bool_and(
                       pg_catalog.pg_get_userbyid(function.proowner)=$1
                       AND NOT function.prosecdef
                       AND function.proconfig=ARRAY['search_path=pg_catalog, pg_temp']::text[]
                       AND NOT EXISTS (
                           SELECT 1 FROM pg_catalog.aclexplode(
                               coalesce(function.proacl,
                                   pg_catalog.acldefault('f',function.proowner))) acl
                            WHERE acl.grantee<>function.proowner))
              FROM pg_catalog.pg_proc function
             WHERE function.pronamespace='public'::pg_catalog.regnamespace
               AND function.proname IN ('enforce_price_point_ingestion_fence',
                   'enforce_inflation_rate_ingestion_fence','enforce_saved_scenario_hard_cap')
            """, cancellationToken, options.Contract.Owner.Name);

        await AssertSecurityFingerprintAsync(connection, "timescale_internal_schema_acl_mismatch", """
            WITH target AS (
                SELECT namespace.nspowner,namespace.nspacl,extension.extowner,
                       pg_catalog.pg_get_userbyid(namespace.nspowner) AS owner_name
                  FROM pg_catalog.pg_namespace namespace
                  JOIN pg_catalog.pg_extension extension ON extension.extname='timescaledb'
                 WHERE namespace.nspname='_timescaledb_internal'),
            actual AS (
                SELECT coalesce(grantee.rolname,'PUBLIC') AS grantee,
                       grantor.rolname AS grantor,acl.privilege_type,acl.is_grantable
                  FROM target
                  CROSS JOIN LATERAL pg_catalog.aclexplode(
                      coalesce(target.nspacl,
                          pg_catalog.acldefault('n',target.nspowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor),
            expected_owner AS (
                SELECT owner_name AS grantee,owner_name AS grantor,
                       'CREATE'::text AS privilege_type,false AS is_grantable FROM target
                UNION ALL
                SELECT owner_name,owner_name,'USAGE'::text,false FROM target),
            usage_roles(grantee) AS (
                SELECT unnest(ARRAY[$1,$2,$3,$4,$5,$6,$7,$8]::text[])),
            expected AS (
                SELECT * FROM expected_owner
                UNION ALL
                SELECT usage_roles.grantee,target.owner_name,
                       'USAGE'::text,false
                  FROM usage_roles CROSS JOIN target)
            SELECT (SELECT count(*)=1 AND bool_and(nspowner=extowner) FROM target)
               AND NOT EXISTS ((SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)
                               UNION ALL
                               (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual))
            """, cancellationToken,
            options.Contract.TimescaleScheduler.Name,
            options.Contract.MigratorCapability.Name,
            options.Contract.ApiCapability.Name,
            options.Contract.IngestionCapability.Name,
            options.Contract.CalendarImporterCapability.Name,
            options.Contract.ExporterCapability.Name,
            options.Contract.AuditCapability.Name,
            options.Contract.Owner.Name);

        await AssertSecurityFingerprintAsync(connection, "timescale_chunk_security_mismatch", """
            WITH chunk_names AS (
                SELECT chunks.chunk_schema AS schema_name,chunks.chunk_name AS table_name,
                       chunks.hypertable_name
                  FROM timescaledb_information.chunks chunks
                 WHERE chunks.hypertable_schema='public'
                   AND chunks.hypertable_name IN ('price_points','activity_logs')
                UNION
                SELECT compressed.schema_name,compressed.table_name,hypertable.table_name
                  FROM _timescaledb_catalog.chunk source
                  JOIN _timescaledb_catalog.chunk compressed
                    ON compressed.id=source.compressed_chunk_id
                  JOIN _timescaledb_catalog.hypertable hypertable
                    ON hypertable.id=source.hypertable_id
                 WHERE hypertable.schema_name='public'
                   AND hypertable.table_name IN ('price_points','activity_logs')
                   AND NOT compressed.dropped),
            base_expected(hypertable_name,grantee,privilege_type,grantor) AS (VALUES
                ('price_points',$3,'SELECT',$1),
                ('price_points',$4,'SELECT',$1),
                ('price_points',$5,'SELECT',$1),
                ('activity_logs',$3,'INSERT',$2)),
            legacy_expected(hypertable_name,grantee,privilege_type,grantor) AS (VALUES
                ('price_points',$4,'INSERT',$1),
                ('price_points',$4,'UPDATE',$1)),
            expected AS (
                SELECT * FROM base_expected
                UNION ALL SELECT * FROM legacy_expected WHERE NOT $6),
            relations AS (
                SELECT chunk_names.*,relation.oid,relation.relowner,relation.relacl,
                       relation.relrowsecurity,relation.relforcerowsecurity
                  FROM chunk_names
                  LEFT JOIN pg_catalog.pg_namespace namespace
                    ON namespace.nspname=chunk_names.schema_name
                  LEFT JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace=namespace.oid
                   AND relation.relname=chunk_names.table_name),
            expected_by_chunk AS (
                SELECT relations.schema_name,relations.table_name,
                       expected.grantee,expected.privilege_type,expected.grantor
                  FROM relations
                  JOIN expected USING (hypertable_name)),
            actual AS (
                SELECT relations.schema_name,relations.table_name,
                       grantee.rolname AS grantee,acl.privilege_type,
                       grantor.rolname AS grantor,acl.is_grantable
                  FROM relations
                  CROSS JOIN LATERAL pg_catalog.aclexplode(
                      coalesce(relations.relacl,
                          pg_catalog.acldefault('r',relations.relowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE acl.grantee<>relations.relowner)
            SELECT NOT EXISTS (
                       SELECT 1 FROM relations
                        WHERE oid IS NULL OR pg_catalog.pg_get_userbyid(relowner)<>
                              CASE WHEN hypertable_name='activity_logs' THEN $2 ELSE $1 END
                           OR relrowsecurity OR relforcerowsecurity
                           OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy
                                       WHERE policy.polrelid=relations.oid))
               AND NOT EXISTS (
                       SELECT 1
                         FROM actual
                         FULL JOIN expected_by_chunk expected_acl
                           ON expected_acl.schema_name=actual.schema_name
                          AND expected_acl.table_name=actual.table_name
                          AND expected_acl.grantee=actual.grantee
                          AND expected_acl.privilege_type=actual.privilege_type
                        WHERE actual.schema_name IS NULL
                           OR expected_acl.schema_name IS NULL
                           OR actual.grantor<>expected_acl.grantor
                           OR actual.is_grantable)
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.TimescaleScheduler.Name,
            options.Contract.ApiCapability.Name,
            options.Contract.IngestionCapability.Name,
            options.Contract.AuditCapability.Name,
            requirePriceAuthority);

        await AssertSecurityFingerprintAsync(connection, "compression_policy_owner_mismatch", """
            SELECT count(*)=1 AND bool_and(owner::text=$1 AND proc_name='policy_compression'
                       AND schedule_interval=INTERVAL '12 hours' AND scheduled
                       AND config->>'compress_after'='7 days')
              FROM timescaledb_information.jobs
             WHERE hypertable_schema='public' AND hypertable_name='activity_logs'
            """, cancellationToken, options.Contract.TimescaleScheduler.Name);
    }

    private static async Task AssertSecurityFingerprintAsync(
        NpgsqlConnection connection,
        string code,
        string sql,
        CancellationToken cancellationToken,
        params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new MigratorRejectedException("schema_fingerprint_mismatch", code);
    }

    private async Task VerifyPriceAuthorityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await AssertSecurityFingerprintAsync(connection, "price_authority_column_mismatch", """
            WITH expected(relation_name,column_name,type_name,not_null,default_expression) AS (VALUES
                ('price_points','provider_source','character varying(32)',false,NULL),
                ('price_points','source_observation_id','character varying(256)',false,NULL),
                ('price_points','as_of_at','timestamp with time zone',false,NULL),
                ('price_points','price_kind','character varying(32)',false,NULL),
                ('price_points','is_final','boolean',false,NULL),
                ('price_points','observation_sha256','bytea',false,NULL),
                ('price_points','authority_contract_version','integer',false,NULL),
                ('inflation_rates','provider_source','character varying(32)',false,NULL),
                ('inflation_rates','source_observation_id','character varying(256)',false,NULL),
                ('inflation_rates','as_of_at','timestamp with time zone',false,NULL),
                ('inflation_rates','price_kind','character varying(32)',false,NULL),
                ('inflation_rates','is_final','boolean',false,NULL),
                ('inflation_rates','observation_sha256','bytea',false,NULL),
                ('inflation_rates','authority_contract_version','integer',false,NULL),
                ('inflation_rates','source_raw','jsonb',false,NULL),
                ('provider_fetch_payloads','provider_source','character varying(32)',true,NULL),
                ('provider_fetch_payloads','payload_sha256','bytea',true,NULL),
                ('provider_fetch_payloads','payload_byte_length','integer',true,NULL),
                ('provider_fetch_payloads','first_observed_at','timestamp with time zone',true,'clock_timestamp()'),
                ('price_observation_attributions','asset_id','uuid',true,NULL),
                ('price_observation_attributions','price_date','date',true,NULL),
                ('price_observation_attributions','ingestion_window_id','uuid',true,NULL),
                ('price_observation_attributions','provider_source','character varying(32)',true,NULL),
                ('price_observation_attributions','payload_sha256','bytea',true,NULL),
                ('price_observation_attributions','source_observation_id','character varying(256)',true,NULL),
                ('price_observation_attributions','observation_sha256','bytea',true,NULL),
                ('price_observation_attributions','authority_contract_version','integer',true,NULL),
                ('price_observation_attributions','attributed_at','timestamp with time zone',true,'clock_timestamp()'),
                ('inflation_observation_attributions','period_date','date',true,NULL),
                ('inflation_observation_attributions','source','character varying(20)',true,NULL),
                ('inflation_observation_attributions','ingestion_window_id','uuid',true,NULL),
                ('inflation_observation_attributions','provider_source','character varying(32)',true,NULL),
                ('inflation_observation_attributions','payload_sha256','bytea',true,NULL),
                ('inflation_observation_attributions','source_observation_id','character varying(256)',true,NULL),
                ('inflation_observation_attributions','observation_sha256','bytea',true,NULL),
                ('inflation_observation_attributions','authority_contract_version','integer',true,NULL),
                ('inflation_observation_attributions','attributed_at','timestamp with time zone',true,'clock_timestamp()')),
            actual AS (
                SELECT relation.relname,attribute.attname,
                       pg_catalog.format_type(attribute.atttypid,attribute.atttypmod),
                       attribute.attnotnull,
                       pg_catalog.pg_get_expr(default_value.adbin,default_value.adrelid)
                  FROM expected
                  LEFT JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace='public'::pg_catalog.regnamespace
                   AND relation.relname=expected.relation_name
                  LEFT JOIN pg_catalog.pg_attribute attribute
                    ON attribute.attrelid=relation.oid AND attribute.attname=expected.column_name
                   AND attribute.attnum>0 AND NOT attribute.attisdropped
                  LEFT JOIN pg_catalog.pg_attrdef default_value
                    ON default_value.adrelid=relation.oid AND default_value.adnum=attribute.attnum),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "price_authority_constraint_mismatch", """
            WITH expected(name,relation_name,kind,validated,delete_action,definition_sha256) AS (VALUES
                ('chk_price_points_authority_tuple','price_points','c',false,NULL,'56d37a4074f20e538a6a32bfef3dba6271160b82544b672aa2bbe12e744bf3e5'),
                ('chk_price_points_provider_kind','price_points','c',false,NULL,'15dbb9012ff5cb5411e43c4abad1790214399ed5dad677715f0c2b8350feae5a'),
                ('chk_price_points_numeric','price_points','c',false,NULL,'dca4038f346c80c13292b92135b6f1640b68a57ba2589e4925a7d26b5e556f57'),
                ('chk_price_points_provider_shape','price_points','c',false,NULL,'56e7e2dfd5a083c5cb3eb14e1765b6bf7256e1d2a16d1705b52f82f2b94d6af0'),
                ('chk_price_points_as_of','price_points','c',false,NULL,'c9191bfeb8179e38f6376eeb79817d89d5d69e99c755d86680fd83122c434268'),
                ('chk_inflation_rates_authority_tuple','inflation_rates','c',false,NULL,'5cc232a700955c8093c1c7b376391000d08bbb80353a5b24fb51ffe2da8609fb'),
                ('chk_inflation_rates_numeric','inflation_rates','c',false,NULL,'995968bcc5179b2e8f517449d3f678d995e6404ed6eb2bbafe4414ef21cf8291'),
                ('chk_inflation_rates_as_of','inflation_rates','c',false,NULL,'6550f93376e8d144fadd75f8a7a3c2d647c2e530950ef6e3192c9cecf97b4b09'),
                ('pk_provider_fetch_payloads','provider_fetch_payloads','p',true,NULL,'ae4dce90881dda440e15c9840a665698f9fed0011b58c994ffc4ef63b9d45e2e'),
                ('chk_provider_fetch_payloads_source','provider_fetch_payloads','c',true,NULL,'1a8d522a3b16a2e9fd38450274f6fe071ddf01d2c6edb8b7c071817c6f95bb75'),
                ('chk_provider_fetch_payloads_sha','provider_fetch_payloads','c',true,NULL,'6cdfef74f1ab94b20d2db88df8cb88922d7cd7008cb5150b88aa87a7e7acba9e'),
                ('chk_provider_fetch_payloads_length','provider_fetch_payloads','c',true,NULL,'d77ba96b6b4c91f0831468e8c3cf3b5297f204c7221282a417a95d5425749fd3'),
                ('pk_price_observation_attributions','price_observation_attributions','p',true,NULL,'a30023c43fe95b7621c55f48c10ec2760357c3d6f5c00055f075d41eaefcd86e'),
                ('fk_price_attribution_window','price_observation_attributions','f',true,'r','299d64815f6570c929dc5a64fe99e4767fba85245c79e9359b8c4459e80986fb'),
                ('fk_price_attribution_payload','price_observation_attributions','f',true,'r','4daaa036b74238e40841fb481de72012e80fbeec6e337407896cdcd8b2b147f2'),
                ('chk_price_attribution_sha','price_observation_attributions','c',true,NULL,'e823465c0b70501b8ad6632cf6f7295d745a6dc22114f30cd53f04f57a9181f6'),
                ('chk_price_attribution_contract','price_observation_attributions','c',true,NULL,'a0a9e709a8141cb987cb991ea9109dc01947d7b3e065872ac4d3a2e92d41c8e0'),
                ('pk_inflation_observation_attributions','inflation_observation_attributions','p',true,NULL,'d437b49347be7b3a384d39a06afc0e019e9cf0205c01c1d65119a3bcb3f2f928'),
                ('fk_inflation_attribution_observation','inflation_observation_attributions','f',true,'r','80cd8c0196e1ab6b7cac97e777f6e309c5dd2cef5d3a50d21c03a414ac90a665'),
                ('fk_inflation_attribution_window','inflation_observation_attributions','f',true,'r','299d64815f6570c929dc5a64fe99e4767fba85245c79e9359b8c4459e80986fb'),
                ('fk_inflation_attribution_payload','inflation_observation_attributions','f',true,'r','4daaa036b74238e40841fb481de72012e80fbeec6e337407896cdcd8b2b147f2'),
                ('chk_inflation_attribution_sha','inflation_observation_attributions','c',true,NULL,'e823465c0b70501b8ad6632cf6f7295d745a6dc22114f30cd53f04f57a9181f6'),
                ('chk_inflation_attribution_contract','inflation_observation_attributions','c',true,NULL,'a0a9e709a8141cb987cb991ea9109dc01947d7b3e065872ac4d3a2e92d41c8e0')),
            actual AS (
                SELECT contract.conname,relation.relname,contract.contype::text,
                       contract.convalidated,
                       CASE WHEN contract.contype='f' THEN contract.confdeltype::text END,
                       pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                           pg_catalog.pg_get_constraintdef(contract.oid,true),'UTF8')),'hex')
                  FROM expected
                  LEFT JOIN pg_catalog.pg_constraint contract
                    ON contract.connamespace='public'::pg_catalog.regnamespace
                   AND contract.conname=expected.name
                  LEFT JOIN pg_catalog.pg_class relation ON relation.oid=contract.conrelid),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
               AND NOT EXISTS (
                   SELECT 1 FROM pg_catalog.pg_index index
                   JOIN pg_catalog.pg_class relation ON relation.oid=index.indrelid
                   JOIN pg_catalog.pg_class index_relation ON index_relation.oid=index.indexrelid
                  WHERE relation.relnamespace='public'::pg_catalog.regnamespace
                    AND relation.relname IN ('provider_fetch_payloads','price_observation_attributions',
                                             'inflation_observation_attributions')
                    AND (NOT index.indisprimary OR NOT index.indisunique OR NOT index.indisvalid
                         OR NOT index.indisready OR index_relation.relname NOT LIKE 'pk_%'))
               AND (SELECT count(*) FROM pg_catalog.pg_index index
                    JOIN pg_catalog.pg_class relation ON relation.oid=index.indrelid
                   WHERE relation.relnamespace='public'::pg_catalog.regnamespace
                     AND relation.relname IN ('provider_fetch_payloads','price_observation_attributions',
                                              'inflation_observation_attributions'))=3
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "price_authority_trigger_mismatch", """
            WITH expected(relation_name,trigger_name,function_schema,function_name,trigger_type,enabled) AS (VALUES
                ('price_points','ts_insert_blocker','_timescaledb_functions','insert_blocker',7,'O'),
                ('price_points','trg_price_points_ingestion_fence','public','enforce_price_point_ingestion_fence',23,'O'),
                ('price_points','trg_price_points_authority','public','enforce_price_point_authority',23,'O'),
                ('inflation_rates','trg_inflation_rates_ingestion_fence','public','enforce_inflation_rate_ingestion_fence',23,'A'),
                ('inflation_rates','trg_inflation_rates_authority','public','enforce_inflation_rate_authority',23,'A'),
                ('price_observation_attributions','trg_price_attribution_append_only','public','enforce_observation_attribution',31,'O'),
                ('price_observation_attributions','trg_price_attribution_no_truncate','public','reject_fetch_payload_mutation',34,'O'),
                ('inflation_observation_attributions','trg_inflation_attribution_append_only','public','enforce_observation_attribution',31,'O'),
                ('inflation_observation_attributions','trg_inflation_attribution_no_truncate','public','reject_fetch_payload_mutation',34,'O'),
                ('provider_fetch_payloads','trg_fetch_payload_append_only','public','reject_fetch_payload_mutation',27,'O'),
                ('provider_fetch_payloads','trg_fetch_payload_live_lease','public','enforce_fetch_payload_insert',7,'O'),
                ('provider_fetch_payloads','trg_fetch_payload_no_truncate','public','reject_fetch_payload_mutation',34,'O')),
            actual AS (
                SELECT relation.relname,trigger.tgname,function_namespace.nspname,function.proname,
                       trigger.tgtype::integer,trigger.tgenabled::text
                  FROM pg_catalog.pg_class relation
                  JOIN pg_catalog.pg_trigger trigger
                    ON trigger.tgrelid=relation.oid AND NOT trigger.tgisinternal
                  JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
                  JOIN pg_catalog.pg_namespace function_namespace ON function_namespace.oid=function.pronamespace
                 WHERE relation.relnamespace='public'::pg_catalog.regnamespace
                   AND relation.relname IN (SELECT DISTINCT relation_name FROM expected)),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "price_authority_function_security_mismatch", """
            WITH expected(name,identity_arguments,result_type,strict,language,volatility,body_sha256) AS (VALUES
                ('saydin_source_raw_allowed','payload jsonb','boolean',true,'sql','i','b656a6a3ccbe9c0e7172fba6738697f98d68de708d263b1ad25fa73237113d07'),
                ('saydin_canonical_observation','payload jsonb','jsonb',true,'sql','i','33535c05ce918127ab5c98fe0bb4bc90082dbe8f2bb881c61a2a45879869a04a'),
                ('enforce_price_point_authority','','trigger',false,'plpgsql','v','7705a66f958768e4e070fc271569084d0e2bc6b87d145b82609138910d5e9ac4'),
                ('enforce_inflation_rate_authority','','trigger',false,'plpgsql','v','2a7f5fc9469f5e13f3f5f776561030b17d60b8f72ff2fd0d2fadf12139764232'),
                ('enforce_observation_attribution','','trigger',false,'plpgsql','v','1097075efe80dd06651f8911d7cc0a7b99ed028de00888495d89997f04e5bb3b'),
                ('enforce_fetch_payload_insert','','trigger',false,'plpgsql','v','60c2b368883fb285ea9769c7af8c31be81417151425839375e78243311375ce4'),
                ('reject_fetch_payload_mutation','','trigger',false,'plpgsql','v','50e1f311966cc9298ad4d41986c552526d4bfd911527d93db85da59acf71eaf4'))
            SELECT count(*)=7 AND count(function.oid)=7 AND bool_and(
                       pg_catalog.pg_get_userbyid(function.proowner)=$1
                       AND pg_catalog.pg_get_function_identity_arguments(function.oid)=expected.identity_arguments
                       AND pg_catalog.pg_get_function_result(function.oid)=expected.result_type
                       AND function.proisstrict=expected.strict AND function.prokind='f'
                       AND language.lanname=expected.language
                       AND function.provolatile=expected.volatility
                       AND function.proparallel='u' AND NOT function.proleakproof
                       AND NOT function.prosecdef
                       AND function.proconfig=ARRAY['search_path=pg_catalog, pg_temp']::text[]
                       AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                           function.prosrc,'UTF8')),'hex')=expected.body_sha256)
              FROM expected
              LEFT JOIN pg_catalog.pg_proc function
                ON function.pronamespace='public'::pg_catalog.regnamespace
               AND function.proname=expected.name
              LEFT JOIN pg_catalog.pg_language language ON language.oid=function.prolang
            """, cancellationToken, options.Contract.Owner.Name);

        await AssertSecurityFingerprintAsync(connection, "price_authority_chunk_mismatch", """
            WITH chunks AS (
                SELECT chunk_schema,chunk_name
                  FROM timescaledb_information.chunks
                 WHERE hypertable_schema='public' AND hypertable_name='price_points'),
            relations AS (
                SELECT chunks.*,relation.oid,relation.relowner
                  FROM chunks
                  LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.nspname=chunks.chunk_schema
                  LEFT JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace=namespace.oid AND relation.relname=chunks.chunk_name),
            expected_trigger(trigger_name,function_name,trigger_type,enabled) AS (VALUES
                ('trg_price_points_authority','enforce_price_point_authority',23,'O'),
                ('trg_price_points_ingestion_fence','enforce_price_point_ingestion_fence',23,'O')),
            expected_by_chunk AS (
                SELECT relations.oid,expected_trigger.*
                  FROM relations CROSS JOIN expected_trigger),
            actual_trigger AS (
                SELECT relations.oid,trigger.tgname,function.proname,
                       trigger.tgtype::integer,trigger.tgenabled::text
                  FROM relations
                  JOIN pg_catalog.pg_trigger trigger
                    ON trigger.tgrelid=relations.oid AND NOT trigger.tgisinternal
                  JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid),
            trigger_differences AS (
                (SELECT * FROM expected_by_chunk EXCEPT ALL SELECT * FROM actual_trigger)
                UNION ALL
                (SELECT * FROM actual_trigger EXCEPT ALL SELECT * FROM expected_by_chunk))
            SELECT NOT EXISTS (SELECT 1 FROM relations
                                WHERE oid IS NULL OR pg_catalog.pg_get_userbyid(relowner)<>$1)
               AND NOT EXISTS (SELECT 1 FROM trigger_differences)
               AND NOT EXISTS (
                    SELECT 1
                      FROM relations
                      JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid=relations.oid
                      CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
                     WHERE attribute.attnum>0 AND NOT attribute.attisdropped)
            """, cancellationToken, options.Contract.Owner.Name);
    }

    private async Task VerifyApiTrustAsync(
        NpgsqlConnection connection,
        bool requireCredentialRehash,
        CancellationToken cancellationToken)
    {
        await AssertSecurityFingerprintAsync(connection, "api_trust_column_mismatch", """
            WITH expected(relation_name,column_name,type_name,not_null,default_expression) AS (VALUES
                ('users','principal_status','character varying(32)',true,'''legacy_quarantined''::character varying'),
                ('users','principal_contract_version','integer',true,'1'),
                ('users','principal_quarantined_at','timestamp with time zone',false,'statement_timestamp()'),
                ('users','principal_revoked_at','timestamp with time zone',false,NULL),
                ('users','principal_expires_at','timestamp with time zone',false,NULL),
                ('installation_credentials','id','uuid',true,NULL),
                ('installation_credentials','principal_id','uuid',true,NULL),
                ('installation_credentials','generation','integer',true,NULL),
                ('installation_credentials','secret_hash','bytea',true,NULL),
                ('installation_credentials','hash_key_version','smallint',true,NULL),
                ('installation_credentials','state','character varying(16)',true,NULL),
                ('installation_credentials','issued_at','timestamp with time zone',true,'clock_timestamp()'),
                ('installation_credentials','pending_expires_at','timestamp with time zone',false,NULL),
                ('installation_credentials','expires_at','timestamp with time zone',false,NULL),
                ('installation_credentials','activated_at','timestamp with time zone',false,NULL),
                ('installation_credentials','revoked_at','timestamp with time zone',false,NULL),
                ('installation_credentials','rotation_parent_id','uuid',false,NULL),
                ('installation_credentials','rotation_id','uuid',false,NULL),
                ('asset_catalog_state','singleton','smallint',true,NULL),
                ('asset_catalog_state','revision','bigint',true,NULL),
                ('asset_catalog_state','catalog_sha256','bytea',true,NULL),
                ('asset_catalog_state','updated_at','timestamp with time zone',true,'clock_timestamp()')),
            actual AS (
                SELECT relation.relname,attribute.attname,
                       pg_catalog.format_type(attribute.atttypid,attribute.atttypmod),
                       attribute.attnotnull,
                       pg_catalog.pg_get_expr(default_value.adbin,default_value.adrelid)
                  FROM expected
                  LEFT JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace='public'::pg_catalog.regnamespace
                   AND relation.relname=expected.relation_name
                  LEFT JOIN pg_catalog.pg_attribute attribute
                    ON attribute.attrelid=relation.oid AND attribute.attname=expected.column_name
                   AND attribute.attnum>0 AND NOT attribute.attisdropped
                  LEFT JOIN pg_catalog.pg_attrdef default_value
                    ON default_value.adrelid=relation.oid AND default_value.adnum=attribute.attnum),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
               AND (SELECT count(*) FROM pg_catalog.pg_attribute
                     WHERE attrelid='public.users'::pg_catalog.regclass
                       AND attnum>0 AND NOT attisdropped)=11
               AND (SELECT count(*) FROM pg_catalog.pg_attribute
                     WHERE attrelid='public.installation_credentials'::pg_catalog.regclass
                       AND attnum>0 AND NOT attisdropped)=13
               AND (SELECT count(*) FROM pg_catalog.pg_attribute
                     WHERE attrelid='public.asset_catalog_state'::pg_catalog.regclass
                       AND attnum>0 AND NOT attisdropped)=4
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "api_trust_constraint_mismatch", """
            WITH expected(name,relation_name,kind,validated,delete_action,definition_sha256) AS (VALUES
                ('chk_users_principal_status','users','c',true,NULL,'722850d068f075cababa9064efe6c46d1bb895f5a2c12d04f478a48b7a8ccbe4'),
                ('chk_users_principal_contract_version','users','c',true,NULL,'a53faab99d6ecc8cd9aee10e842cdb473d977ddee97b83bec8a7d8e495ed7c27'),
                ('chk_users_principal_lifecycle','users','c',true,NULL,'147ed35bdf057c15f501b5e7c323d78c767a8a94f13476c543499d1edc56f514'),
                ('chk_users_principal_expiry','users','c',true,NULL,'7ae79366dd327aaa6ffcf4a8102816dce05ee559ff34f30f9b5f0c91bbb23856'),
                ('installation_credentials_pkey','installation_credentials','p',true,NULL,'8c8464f42472e42ee190fc91ca8db79b5351d3a4609040516578d229c56f6fa5'),
                ('fk_installation_credentials_principal','installation_credentials','f',true,'c','3bd23dcfde6490476c1d0c440bd2fb58c7a5bcf490c73ef34f73c8673e21bfde'),
                ('fk_installation_credentials_rotation_parent','installation_credentials','f',true,'r','b39c6f4a1291e89e845c63e24c850e0eef9ef4fed85985014ed60ca148dbe03d'),
                ('chk_installation_credentials_generation','installation_credentials','c',true,NULL,'200a0506f021418ff18bd03d809684a96a3a050cbefc6fa9a61603904e6128b9'),
                ('chk_installation_credentials_hash_key_version','installation_credentials','c',true,NULL,'4250833d9ad60c81957a79c9c83ba789ede4ede81c6cd69f2558b1bcd70a011c'),
                ('chk_installation_credentials_secret_hash','installation_credentials','c',true,NULL,'164608b6a90795cca646f151db02926202693d32821432b27c0c616089f8754f'),
                ('chk_installation_credentials_state','installation_credentials','c',true,NULL,'fbc062f750093de5e25bd35c64b71990d0f0af9f40b24d5b33c0dcd0e2a4c6d7'),
                ('chk_installation_credentials_lifecycle','installation_credentials','c',true,NULL,'9e6312b1c37ef934c7b77c3f9562852f9c6e8f2b9ca93e18d773ca3a59da32df'),
                ('chk_installation_credentials_expiry','installation_credentials','c',true,NULL,'f5275584c5cf83197357de6b63e3a041df1cc515f01eebdf4096963e51ea9a53'),
                ('chk_installation_credentials_rotation','installation_credentials','c',true,NULL,'20e6911b66b51c1e52924076f4303dd0ad18a1e19c7c5652d6b4865fdf0bf962'),
                ('uq_installation_credentials_verifier','installation_credentials','u',true,NULL,'a8109116b53f8d153c71a5e8ecaceb1307d9072a142ad27af57551b3428cdce2'),
                ('uq_installation_credentials_generation','installation_credentials','u',true,NULL,'9bfc1104ec587f1759bc6ffe651764842a0ffba4380c9d7db7773fb84e4cc6f2'),
                ('asset_catalog_state_pkey','asset_catalog_state','p',true,NULL,'d004b3efcdc4a0108ecbe83c93408f63eebecc563529a3941a4c59667835f25b'),
                ('chk_asset_catalog_state_singleton','asset_catalog_state','c',true,NULL,'ca7ba2ea8fc4d647ecdaff1ffbfd4cd94e0510195f98fd436678a75273aebb3d'),
                ('chk_asset_catalog_state_revision','asset_catalog_state','c',true,NULL,'6e1b5de774e1e089aaa7ec71bc7230076978638860c68fb8a5ca3d3745130265'),
                ('chk_asset_catalog_state_sha256','asset_catalog_state','c',true,NULL,'2861ff73012cf53a26f7c05d5aae143751e66286cdf7ce41a08da9ec8d250f97')),
            actual AS (
                SELECT contract.conname,relation.relname,contract.contype::text,
                       contract.convalidated,
                       CASE WHEN contract.contype='f' THEN contract.confdeltype::text END,
                       pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                           pg_catalog.pg_get_constraintdef(contract.oid,true),'UTF8')),'hex')
                  FROM expected
                  LEFT JOIN pg_catalog.pg_constraint contract
                    ON contract.connamespace='public'::pg_catalog.regnamespace
                   AND contract.conname=expected.name
                  LEFT JOIN pg_catalog.pg_class relation ON relation.oid=contract.conrelid),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
               AND (SELECT count(*) FROM pg_catalog.pg_constraint
                     WHERE conrelid='public.installation_credentials'::pg_catalog.regclass)=12
               AND (SELECT count(*) FROM pg_catalog.pg_constraint
                     WHERE conrelid='public.asset_catalog_state'::pg_catalog.regclass)=4
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "api_trust_index_mismatch", """
            WITH expected(name,relation_name,is_primary,is_unique,key_columns,predicate,definition_sha256) AS (VALUES
                ('installation_credentials_pkey','installation_credentials',true,true,ARRAY['id']::text[],NULL,
                    '96269248b7e49b634cfbe44d9ce85f75b55499fa45453883ef3c35d6325d8052'),
                ('uq_installation_credentials_verifier','installation_credentials',false,true,ARRAY['hash_key_version','secret_hash']::text[],NULL,
                    'd4f4a0c99a1c3396855613d9c66b7d7872e8d8def2364a8ef954a7cf90c33a6c'),
                ('uq_installation_credentials_generation','installation_credentials',false,true,ARRAY['principal_id','generation']::text[],NULL,
                    '4f587bce549f6308e64062cd26af597b9026cb615fe66a9b634f6b0897ebbc7e'),
                ('uq_installation_credentials_active_principal','installation_credentials',false,true,ARRAY['principal_id']::text[],
                    $sql$((state)::text = 'active'::text)$sql$,
                    '3d07ecab089c05603354479edf72292adb3ca66f45d9c425328deb5992f886bf'),
                ('uq_installation_credentials_pending_principal','installation_credentials',false,true,ARRAY['principal_id']::text[],
                    $sql$((state)::text = 'pending'::text)$sql$,
                    'c1f3939319449b77cfaccef5251918e3212fbd95ef65df82ffdb23c370b4b655'),
                ('uq_installation_credentials_rotation_id','installation_credentials',false,true,ARRAY['rotation_id']::text[],
                    $sql$(rotation_id IS NOT NULL)$sql$,
                    '2e1e33f5635e2e16e91893c9e78cdc0af712899ddd29e3dd5b042e7acde96936'),
                ('asset_catalog_state_pkey','asset_catalog_state',true,true,ARRAY['singleton']::text[],NULL,
                    '2cb7520c67de4eb00b6fa4ea24a7130030a117ec63dd7117080f7b55a68c1258')),
            actual AS (
                SELECT index_relation.relname,relation.relname,index.indisprimary,index.indisunique,
                       ARRAY(SELECT attribute.attname
                               FROM unnest(index.indkey::smallint[]) WITH ORDINALITY key(attnum,ordinality)
                               JOIN pg_catalog.pg_attribute attribute
                                 ON attribute.attrelid=index.indrelid AND attribute.attnum=key.attnum
                              WHERE key.ordinality<=index.indnkeyatts ORDER BY key.ordinality),
                       pg_catalog.pg_get_expr(index.indpred,index.indrelid),
                       pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                           pg_catalog.pg_get_indexdef(index.indexrelid),'UTF8')),'hex')
                  FROM expected
                  LEFT JOIN pg_catalog.pg_class index_relation
                    ON index_relation.relnamespace='public'::pg_catalog.regnamespace
                   AND index_relation.relname=expected.name
                  LEFT JOIN pg_catalog.pg_index index ON index.indexrelid=index_relation.oid
                  LEFT JOIN pg_catalog.pg_class relation ON relation.oid=index.indrelid
                 WHERE index.indisvalid AND index.indisready
                   AND index.indexprs IS NULL AND index.indclass[0]<>0),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
               AND (SELECT count(*) FROM pg_catalog.pg_index
                     WHERE indrelid='public.installation_credentials'::pg_catalog.regclass)=6
               AND (SELECT count(*) FROM pg_catalog.pg_index
                     WHERE indrelid='public.asset_catalog_state'::pg_catalog.regclass)=1
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "api_trust_function_security_mismatch", """
            WITH base_expected(
              name,identity_arguments,result_type,language,volatility,body_sha256) AS (VALUES
                ('compute_asset_catalog_sha256','','bytea','sql','s','23d8e0f7e620d3881a279b46b1b61347b4ff54cd20f259c575c26b56f7787efb'),
                ('refresh_asset_catalog_state','','trigger','plpgsql','v','ab2a18e6003ef4cdffa109309bf9e43e0b05bd2184fd2e6198e92bf399c5fb1b'),
                ('register_installation','p_principal_id uuid, p_credential_id uuid, p_secret_hash bytea, p_key_version smallint',
                    'TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)',
                    'plpgsql','v','51c6b4e541e0748d89a3073494759e86b8db129b6d1da566304c1947941de1b4'),
                ('resolve_installation','p_secret_hash bytea, p_key_version smallint',
                    'TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)',
                    'sql','s','faf69e73b08925e06b6af5f3d70285de7be49caa032652eaa7de1bfea4ff1a0d'),
                ('begin_installation_rotation','p_current_secret_hash bytea, p_current_key_version smallint, p_rotation_id uuid, p_new_credential_id uuid, p_new_secret_hash bytea, p_new_key_version smallint',
                    'TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)',
                    'plpgsql','v','87dca3d9934377fb2dbf6135ea4d0909b50c9821a4061c39820661ceadb20ebc'),
                ('commit_installation_rotation','p_rotation_id uuid, p_new_secret_hash bytea, p_new_key_version smallint',
                    'TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)',
                    'plpgsql','v','225d9fab5444eef40a37d368263ad84508a52dfa28727178e20960ec95a8b371'),
                ('revoke_installation','p_secret_hash bytea, p_key_version smallint',
                    'TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)',
                    'plpgsql','v','23632c1b5140a65b8c9aec4850bdc5612da2ff59eca3456a49e004dbf81c7380'),
                ('get_asset_catalog_state','','TABLE(revision bigint, catalog_sha256 bytea, updated_at timestamp with time zone)',
                    'sql','s','1332bd64e33389696ebed64ec1ca9fd96464fdc28693a3efbae0c6068f949c29')),
            credential_rehash_expected(
              name,identity_arguments,result_type,language,volatility,body_sha256) AS (VALUES
                ('resolve_installation_and_rehash','p_secret_hash bytea, p_key_version smallint, p_active_secret_hash bytea, p_active_key_version smallint',
                    'TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)',
                    'plpgsql','v','b009448de892a425e191e649fbd942b6dd77777fa68d9b339b8010cadcbb3de2')),
            expected AS (
              SELECT * FROM base_expected
              UNION ALL SELECT * FROM credential_rehash_expected WHERE $2)
            SELECT count(*)=8 + CASE WHEN $2 THEN 1 ELSE 0 END
               AND count(function.oid)=8 + CASE WHEN $2 THEN 1 ELSE 0 END
               AND bool_and(
                       pg_catalog.pg_get_userbyid(function.proowner)=$1
                       AND pg_catalog.pg_get_function_identity_arguments(function.oid)=expected.identity_arguments
                       AND pg_catalog.pg_get_function_result(function.oid)=expected.result_type
                       AND NOT function.proisstrict AND function.prokind='f'
                       AND language.lanname=expected.language
                       AND function.provolatile=expected.volatility
                       AND function.proparallel='u' AND NOT function.proleakproof
                       AND function.prosecdef
                       AND function.proconfig=ARRAY['search_path=pg_catalog, pg_temp']::text[]
                       AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                           function.prosrc,'UTF8')),'hex')=expected.body_sha256)
              FROM expected
              LEFT JOIN pg_catalog.pg_proc function
                ON function.pronamespace='public'::pg_catalog.regnamespace
               AND function.proname=expected.name
              LEFT JOIN pg_catalog.pg_language language ON language.oid=function.prolang
            """, cancellationToken, options.Contract.Owner.Name, requireCredentialRehash);

        await AssertSecurityFingerprintAsync(connection, "asset_catalog_trigger_mismatch", """
            WITH expected(trigger_name,trigger_type) AS (VALUES
                ('trg_asset_catalog_revision_insert',4),
                ('trg_asset_catalog_revision_update',16),
                ('trg_asset_catalog_revision_delete',8),
                ('trg_asset_catalog_revision_truncate',32)),
            actual AS (
                SELECT trigger.tgname,trigger.tgtype::integer
                  FROM pg_catalog.pg_trigger trigger
                  JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
                 WHERE trigger.tgrelid='public.assets'::pg_catalog.regclass
                   AND NOT trigger.tgisinternal
                   AND (trigger.tgname LIKE 'trg_asset_catalog_revision_%'
                        OR function.proname='refresh_asset_catalog_state')
                   AND trigger.tgenabled='O' AND trigger.tgnargs=0
                   AND trigger.tgqual IS NULL
                   AND trigger.tgattr=''::pg_catalog.int2vector),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "asset_catalog_singleton_mismatch", """
            SELECT count(*)=1 AND bool_and(singleton=1 AND revision>0
                       AND pg_catalog.octet_length(catalog_sha256)=32
                       AND catalog_sha256=public.compute_asset_catalog_sha256()
                       AND updated_at<=pg_catalog.clock_timestamp())
              FROM public.asset_catalog_state
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "installation_principal_lifecycle_mismatch", """
            SELECT NOT EXISTS (
                       SELECT 1 FROM public.users principal
                        WHERE principal.principal_contract_version<>1
                           OR (principal.principal_status='legacy_quarantined' AND EXISTS (
                               SELECT 1 FROM public.installation_credentials credential
                                WHERE credential.principal_id=principal.id))
                           OR (principal.principal_status='active' AND (
                               principal.device_id IS NOT NULL OR
                               (SELECT count(*) FROM public.installation_credentials credential
                                 WHERE credential.principal_id=principal.id
                                   AND credential.state='active')<>1))
                           OR (principal.principal_status<>'active' AND EXISTS (
                               SELECT 1 FROM public.installation_credentials credential
                                WHERE credential.principal_id=principal.id
                                  AND credential.state IN ('active','pending'))))
               AND NOT EXISTS (
                       SELECT 1 FROM public.installation_credentials credential
                       LEFT JOIN public.installation_credentials parent
                         ON parent.id=credential.rotation_parent_id
                        WHERE (credential.state='pending' AND (
                                   parent.id IS NULL OR parent.principal_id<>credential.principal_id
                                   OR parent.generation>=credential.generation
                                   OR parent.state<>'active'))
                           OR (credential.rotation_parent_id IS NULL AND credential.generation<>1))
            """, cancellationToken);
    }

    private async Task VerifyPrincipalRetentionAsync(
        NpgsqlConnection connection,
        bool requireApiSecurityAdmission,
        CancellationToken cancellationToken)
    {
        await AssertSecurityFingerprintAsync(connection, "principal_retention_function_mismatch", """
            WITH expected_acl(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                    ($1,$1,'EXECUTE',false)),
            actual_acl AS (
                SELECT coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM pg_catalog.pg_proc function
                  CROSS JOIN LATERAL pg_catalog.aclexplode(
                      coalesce(function.proacl,
                          pg_catalog.acldefault('f',function.proowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE function.oid=
                       'public.redact_activity_logs_before_principal_delete()'::regprocedure),
            acl_differences AS (
                (SELECT * FROM expected_acl EXCEPT ALL SELECT * FROM actual_acl)
                UNION ALL
                (SELECT * FROM actual_acl EXCEPT ALL SELECT * FROM expected_acl))
            SELECT (SELECT count(*)=1 AND bool_and(
                               pg_catalog.pg_get_userbyid(function.proowner)=$1
                               AND pg_catalog.pg_get_function_identity_arguments(function.oid)=''
                               AND pg_catalog.pg_get_function_result(function.oid)='trigger'
                               AND language.lanname='plpgsql'
                               AND function.provolatile='v' AND function.proparallel='u'
                               AND NOT function.proisstrict AND NOT function.proleakproof
                               AND function.prokind='f' AND function.prosecdef
                               AND function.proconfig=
                                   ARRAY['search_path=pg_catalog, pg_temp']::text[]
                               AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                                   function.prosrc,'UTF8')),'hex')=
                                   'be2799e95d3e4abc7621598bcc116b0f8d5df0a931e4e1c5af6cb2c42cae66e6')
                      FROM pg_catalog.pg_proc function
                      JOIN pg_catalog.pg_language language ON language.oid=function.prolang
                     WHERE function.pronamespace='public'::regnamespace
                       AND function.proname='redact_activity_logs_before_principal_delete')
               AND NOT EXISTS (SELECT 1 FROM acl_differences)
            """, cancellationToken, options.Contract.TimescaleScheduler.Name);

        await AssertSecurityFingerprintAsync(connection, "principal_retention_trigger_mismatch", """
            SELECT count(*)=1 AND bool_and(
                       trigger.tgname='trg_users_principal_retention_redact'
                       AND trigger.tgtype=11 AND trigger.tgenabled='O'
                       AND trigger.tgnargs=0 AND trigger.tgqual IS NULL
                       AND trigger.tgattr=''::pg_catalog.int2vector
                       AND function.oid=
                           'public.redact_activity_logs_before_principal_delete()'::regprocedure)
              FROM pg_catalog.pg_trigger trigger
              JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
             WHERE trigger.tgrelid='public.users'::regclass
               AND NOT trigger.tgisinternal
               AND (trigger.tgname='trg_users_principal_retention_redact'
                    OR function.proname='redact_activity_logs_before_principal_delete')
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "principal_retention_fk_mismatch", """
            SELECT count(*)=1 AND bool_and(
                       constraint_row.contype='f'
                       AND constraint_row.confrelid='public.users'::regclass
                       AND constraint_row.conkey=ARRAY[
                           (SELECT attnum FROM pg_catalog.pg_attribute
                             WHERE attrelid='public.activity_logs'::regclass
                               AND attname='user_id')]::smallint[]
                       AND constraint_row.confkey=ARRAY[
                           (SELECT attnum FROM pg_catalog.pg_attribute
                             WHERE attrelid='public.users'::regclass
                               AND attname='id')]::smallint[]
                       AND constraint_row.convalidated
                       AND NOT constraint_row.condeferrable
                       AND NOT constraint_row.condeferred
                       AND constraint_row.confdeltype='a'
                       AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                           pg_catalog.pg_get_constraintdef(constraint_row.oid,true),'UTF8')),'hex')=
                           '35bba6df01802e7850bd1a753b95ff643a2a01ec56aa476981cbe9dc42705cf3')
              FROM pg_catalog.pg_constraint constraint_row
             WHERE constraint_row.conrelid='public.activity_logs'::regclass
               AND constraint_row.conname='activity_logs_user_id_fkey'
            """, cancellationToken);

        await AssertSecurityFingerprintAsync(connection, "principal_retention_acl_mismatch", """
            WITH relation_set AS (
                SELECT 'public.activity_logs'::regclass AS oid
                UNION
                SELECT pg_catalog.format('%I.%I',chunk.chunk_schema,chunk.chunk_name)::regclass
                  FROM timescaledb_information.chunks chunk
                 WHERE chunk.hypertable_schema='public'
                   AND chunk.hypertable_name='activity_logs'
                UNION
                SELECT relation.oid
                  FROM _timescaledb_catalog.hypertable source
                  JOIN _timescaledb_catalog.hypertable compressed
                    ON compressed.id=source.compressed_hypertable_id
                  JOIN pg_catalog.pg_namespace namespace
                    ON namespace.nspname=compressed.schema_name
                  JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace=namespace.oid
                   AND relation.relname=compressed.table_name
                 WHERE source.schema_name='public' AND source.table_name='activity_logs'
                UNION
                SELECT relation.oid
                  FROM _timescaledb_catalog.hypertable source
                  JOIN _timescaledb_catalog.chunk source_chunk
                    ON source_chunk.hypertable_id=source.id
                   AND source_chunk.compressed_chunk_id IS NOT NULL
                  JOIN _timescaledb_catalog.chunk compressed_chunk
                    ON compressed_chunk.id=source_chunk.compressed_chunk_id
                  JOIN pg_catalog.pg_namespace namespace
                    ON namespace.nspname=compressed_chunk.schema_name
                  JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace=namespace.oid
                   AND relation.relname=compressed_chunk.table_name
                 WHERE source.schema_name='public' AND source.table_name='activity_logs'),
            expected_acl(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ($1,$2,'INSERT',false),($2,$2,'SELECT',false),($2,$2,'UPDATE',false)),
            expected_relation_acl AS (
                SELECT relation_set.oid,expected_acl.*
                  FROM relation_set CROSS JOIN expected_acl
                UNION ALL
                SELECT relation_set.oid,$2,$2,'TRIGGER',false
                  FROM relation_set WHERE $3),
            actual_acl AS (
                SELECT relation.oid,coalesce(grantee.rolname,'PUBLIC'),grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM relation_set
                  JOIN pg_catalog.pg_class relation ON relation.oid=relation_set.oid
                  CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor),
            acl_differences AS (
                (SELECT * FROM expected_relation_acl EXCEPT ALL SELECT * FROM actual_acl)
                UNION ALL
                (SELECT * FROM actual_acl EXCEPT ALL
                 SELECT * FROM expected_relation_acl))
            SELECT (SELECT count(*)>0 AND count(*)=count(relation.oid) AND bool_and(
                               pg_catalog.pg_get_userbyid(relation.relowner)=$2
                               AND NOT relation.relrowsecurity
                               AND NOT relation.relforcerowsecurity)
                      FROM relation_set
                      LEFT JOIN pg_catalog.pg_class relation ON relation.oid=relation_set.oid)
               AND NOT EXISTS (SELECT 1 FROM acl_differences)
               AND NOT EXISTS (
                    SELECT 1 FROM relation_set
                    JOIN pg_catalog.pg_attribute attribute
                      ON attribute.attrelid=relation_set.oid
                    CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
                    WHERE attribute.attnum>0 AND NOT attribute.attisdropped)
            """, cancellationToken, options.Contract.ApiCapability.Name,
            options.Contract.TimescaleScheduler.Name, requireApiSecurityAdmission);

        await AssertSecurityFingerprintAsync(connection, "principal_retention_transition_residual", """
            SELECT pg_catalog.to_regnamespace('saydin_principal_retention_control') IS NULL
               AND NOT pg_catalog.has_schema_privilege($1,'public','CREATE')
               AND NOT pg_catalog.has_schema_privilege($1,'_timescaledb_internal','CREATE')
               AND NOT pg_catalog.has_table_privilege($2,'public.users','DELETE')
               AND (SELECT count(*)=1 AND bool_and(compression_enabled)
                      FROM timescaledb_information.hypertables
                     WHERE hypertable_schema='public' AND hypertable_name='activity_logs')
               AND (SELECT count(*)=1 AND bool_and(job.scheduled
                           AND job.config->>'compress_after'='7 days')
                      FROM timescaledb_information.jobs job
                     WHERE job.hypertable_schema='public'
                       AND job.hypertable_name='activity_logs'
                       AND job.proc_name='policy_compression')
            """, cancellationToken, options.Contract.TimescaleScheduler.Name,
            options.Contract.ApiCapability.Name);
    }

    private async Task VerifyApiSecurityAdmissionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await AssertSecurityFingerprintAsync(
            connection, "api_security_activity_trigger_mismatch", """
            SELECT NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_constraint contract
                        WHERE contract.conrelid='public.activity_logs'::pg_catalog.regclass
                          AND contract.conname='chk_activity_action')
               AND (SELECT count(*)=1 AND bool_and(
                           trigger.tgenabled='O' AND trigger.tgtype=23
                           AND trigger.tgattr::text=attribute.attnum::text
                           AND function.proname='enforce_activity_action_allowlist'
                           AND function.pronamespace='public'::pg_catalog.regnamespace
                           AND pg_catalog.pg_get_userbyid(function.proowner)=$1
                           AND pg_catalog.pg_get_userbyid(relation.relowner)=$1)
                      FROM pg_catalog.pg_trigger trigger
                      JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
                      JOIN pg_catalog.pg_class relation ON relation.oid=trigger.tgrelid
                      JOIN pg_catalog.pg_attribute attribute
                        ON attribute.attrelid=trigger.tgrelid AND attribute.attname='action'
                       AND attribute.attnum>0 AND NOT attribute.attisdropped
                     WHERE trigger.tgrelid='public.activity_logs'::pg_catalog.regclass
                       AND trigger.tgname='trg_activity_action_allowlist'
                       AND NOT trigger.tgisinternal)
            """, cancellationToken, options.Contract.TimescaleScheduler.Name);

        await AssertSecurityFingerprintAsync(
            connection, "api_security_function_mismatch", """
            WITH expected(name,identity_arguments,result_type,strict,language,volatility,
                          parallel,security_definer,body_sha256) AS (VALUES
                ('installation_verifier_matches','p_expected bytea, p_candidate bytea',
                 'boolean',true,'plpgsql','i','s',false,
                 '0fd89e2c59f51af516bc0a028699f24e454dba6c9b37c2e1dd0ab23e82fa1c09'),
                ('resolve_installation_rotation_commit',
                 'p_rotation_id uuid, p_secret_hash bytea, p_key_version smallint',
                 'TABLE(principal_id uuid, credential_id uuid, generation integer, tier character varying, principal_status character varying, credential_state character varying)',
                 false,'sql','s','u',true,
                 '00da525d7b48949f14d10ffee3b21989d9cbf6f47d201b59043c83b13c8386b1'))
            SELECT count(*)=2 AND count(function.oid)=2 AND bool_and(
                       pg_catalog.pg_get_userbyid(function.proowner)=$1
                       AND pg_catalog.pg_get_function_identity_arguments(function.oid)=
                           expected.identity_arguments
                       AND pg_catalog.pg_get_function_result(function.oid)=expected.result_type
                       AND function.proisstrict=expected.strict AND function.prokind='f'
                       AND language.lanname=expected.language
                       AND function.provolatile=expected.volatility
                       AND function.proparallel=expected.parallel
                       AND NOT function.proleakproof
                       AND function.prosecdef=expected.security_definer
                       AND function.proconfig=ARRAY['search_path=pg_catalog, pg_temp']::text[]
                       AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                           function.prosrc,'UTF8')),'hex')=expected.body_sha256)
              FROM expected
              LEFT JOIN pg_catalog.pg_proc function
                ON function.pronamespace='public'::pg_catalog.regnamespace
               AND function.proname=expected.name
              LEFT JOIN pg_catalog.pg_language language ON language.oid=function.prolang
            """, cancellationToken, options.Contract.Owner.Name);

        await AssertSecurityFingerprintAsync(
            connection, "api_security_activity_function_mismatch", """
            SELECT count(*)=1 AND bool_and(
                       pg_catalog.pg_get_userbyid(function.proowner)=$1
                       AND pg_catalog.pg_get_function_identity_arguments(function.oid)=''
                       AND pg_catalog.pg_get_function_result(function.oid)='trigger'
                       AND NOT function.proisstrict AND function.prokind='f'
                       AND language.lanname='plpgsql' AND function.provolatile='v'
                       AND function.proparallel='u' AND NOT function.proleakproof
                       AND NOT function.prosecdef
                       AND function.proconfig=ARRAY['search_path=pg_catalog, pg_temp']::text[]
                       AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                           function.prosrc,'UTF8')),'hex')=
                           'e3bef3c7edc15170f84e99e69683b0ac32e87e023e3416eac2cbafbbd70d3fcc')
              FROM pg_catalog.pg_proc function
              JOIN pg_catalog.pg_language language ON language.oid=function.prolang
             WHERE function.pronamespace='public'::pg_catalog.regnamespace
               AND function.proname='enforce_activity_action_allowlist'
            """, cancellationToken, options.Contract.TimescaleScheduler.Name);

        await AssertSecurityFingerprintAsync(
            connection, "api_security_function_acl_mismatch", """
            WITH expected(function_name,grantor,grantee,privilege_type,is_grantable) AS (VALUES
                ('resolve_installation_rotation_commit',$1::text,$2::text,'EXECUTE',false)),
            functions AS (
                SELECT function.oid,function.proname,function.proowner,function.proacl
                  FROM pg_catalog.pg_proc function
                 WHERE function.pronamespace='public'::pg_catalog.regnamespace
                   AND function.proname IN ('installation_verifier_matches',
                                            'resolve_installation_rotation_commit',
                                            'enforce_activity_action_allowlist')),
            actual AS (
                SELECT functions.proname,grantor.rolname,grantee.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM functions
                  CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                      functions.proacl,pg_catalog.acldefault('f',functions.proowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                 WHERE acl.grantee<>functions.proowner),
            differences AS ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected))
            SELECT (SELECT count(*) FROM functions)=3
               AND NOT EXISTS (
                   SELECT 1 FROM functions
                    WHERE pg_catalog.pg_get_userbyid(proowner)<>
                          CASE WHEN proname='enforce_activity_action_allowlist'
                               THEN $3 ELSE $1 END)
               AND NOT EXISTS (SELECT 1 FROM differences)
            """, cancellationToken, options.Contract.Owner.Name,
            options.Contract.ApiCapability.Name,
            options.Contract.TimescaleScheduler.Name);
    }

    private async Task AcquireLockAsync(
        NpgsqlConnection connection,
        long key,
        string purpose,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < options.Timeouts.Lock)
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
            command.Parameters.AddWithValue(key);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false))
                return;
            await Task.Delay(options.Timeouts.LockPoll, cancellationToken);
        }
        throw new MigratorRejectedException($"{purpose}_lock_timeout");
    }

    private static async Task TryReleaseLockAsync(
        NpgsqlConnection connection,
        long key,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            return;
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
            command.Parameters.AddWithValue(key);
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Session close is the authoritative fallback release mechanism.
        }
    }

    private void VerifyConnectedTarget(TargetIdentity target)
    {
        var actualSystemHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(target.SystemIdentifier)));
        if (!string.Equals(target.Database, options.Database, StringComparison.Ordinal) ||
            !CryptographicEquals(actualSystemHash, options.Contract.SystemIdentifierSha256))
            throw new MigratorRejectedException("migrator_target_contract_mismatch");
    }

    private static long ContractLockKey(string targetLockHash) =>
        unchecked((long)Convert.ToUInt64(targetLockHash[..16], 16));

    private static async Task MarkRunningAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.schema_migrations
                (version, applied_at, checksum, state, error_code, started_at, completed_at)
            VALUES ($1, now(), $2, 'running', NULL, now(), NULL)
            ON CONFLICT (version) DO UPDATE
            SET checksum = EXCLUDED.checksum,
                state = 'running',
                error_code = NULL,
                started_at = now(),
                completed_at = NULL
            """, connection);
        command.Parameters.AddWithValue(migration.Version);
        command.Parameters.AddWithValue(migration.Checksum);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkTerminalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationDefinition migration,
        string state,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public.schema_migrations
            SET checksum = $1, state = $2, error_code = NULL,
                applied_at = now(), completed_at = now()
            WHERE version = $3
            """, connection, transaction);
        command.Parameters.AddWithValue(migration.Checksum);
        command.Parameters.AddWithValue(state);
        command.Parameters.AddWithValue(migration.Version);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new MigratorRejectedException("migration_tracking_row_missing", migration.Version);
    }

    private static async Task TryMarkFailedAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            return;
        try
        {
            await using var command = new NpgsqlCommand("""
                UPDATE public.schema_migrations
                SET state = 'failed', error_code = $1, completed_at = now()
                WHERE version = $2 AND checksum = $3
                """, connection);
            command.Parameters.AddWithValue(errorCode);
            command.Parameters.AddWithValue(migration.Version);
            command.Parameters.AddWithValue(migration.Checksum);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Broken sessions leave the durable 'running' marker for rerun reconciliation.
        }
    }

    private static async Task<bool> TryReconcileCommittedAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            return false;
        try
        {
            var state = await ReadMigrationStateAsync(connection, migration.Version, cancellationToken);
            return state is not null && state.State is "succeeded" or "skipped_optional" &&
                   state.Checksum == migration.Checksum;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task SetTransactionTimeoutsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.set_config('lock_timeout', $1, true),
                   pg_catalog.set_config('statement_timeout', $2, true),
                   pg_catalog.set_config('search_path','public,pg_temp',true)
            """, connection, transaction);
        command.Parameters.AddWithValue($"{Math.Ceiling(options.Timeouts.Lock.TotalMilliseconds)}ms");
        command.Parameters.AddWithValue($"{Math.Ceiling(options.Timeouts.Command.TotalMilliseconds)}ms");
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AssertHistoricalTransactionSearchPathAsync(
            connection, transaction, cancellationToken);
    }

    private static async Task AssertHistoricalTransactionSearchPathAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.current_setting('search_path',false),
                   pg_catalog.current_schemas(true),
                   pg_catalog.current_schemas(false)
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("historical_transaction_search_path_mismatch");
        var declared = reader.GetString(0);
        var effective = reader.GetFieldValue<string[]>(1);
        var explicitSchemas = reader.GetFieldValue<string[]>(2);
        if (!string.Equals(declared, "public,pg_temp", StringComparison.Ordinal) ||
            effective is not ["pg_catalog", ..] ||
            explicitSchemas is not ["public", ..])
            throw new MigratorRejectedException("historical_transaction_search_path_mismatch");
    }

    private async Task SetPrivilegeContractAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var hardenSearchPath = new NpgsqlCommand(
                         "SELECT pg_catalog.set_config('search_path','pg_catalog,pg_temp',true)",
                         connection, transaction))
            await hardenSearchPath.ExecuteNonQueryAsync(cancellationToken);
        var privilegeSearchPath = await ScalarAsync<string>(connection,
            "SELECT pg_catalog.current_setting('search_path',false)",
            transaction, cancellationToken);
        if (!string.Equals(privilegeSearchPath, "pg_catalog,pg_temp", StringComparison.Ordinal))
            throw new MigratorRejectedException("privilege_separation_search_path_mismatch");

        var legacyOwner = options.LegacyPrivilegeCutover
            ? await ScalarAsync<string>(connection, "SELECT session_user", transaction, cancellationToken)
            : options.Contract.Owner.Name;
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.set_config('saydin.role_contract_sha256',$1,true),
                   pg_catalog.set_config('saydin.deployment_id',$2,true),
                   pg_catalog.set_config('saydin.system_identifier_sha256',$3,true),
                   pg_catalog.set_config('saydin.role_prefix',$4,true),
                   pg_catalog.set_config('saydin.owner_role',$5,true),
                   pg_catalog.set_config('saydin.migrator_cap_role',$6,true),
                   pg_catalog.set_config('saydin.api_cap_role',$7,true),
                   pg_catalog.set_config('saydin.ingestion_cap_role',$8,true),
                   pg_catalog.set_config('saydin.calendar_importer_cap_role',$9,true),
                   pg_catalog.set_config('saydin.exporter_cap_role',$10,true),
                   pg_catalog.set_config('saydin.audit_cap_role',$11,true),
                   pg_catalog.set_config('saydin.timescale_scheduler_role',$12,true),
                   pg_catalog.set_config('saydin.migrator_login_role',$13,true),
                   pg_catalog.set_config('saydin.migrator_login_version',$14,true),
                   pg_catalog.set_config('saydin.timescaledb_version',$15,true),
                   pg_catalog.set_config('saydin.uuid_ossp_version',$16,true),
                   pg_catalog.set_config('saydin.legacy_privilege_cutover',$17,true),
                   pg_catalog.set_config('saydin.legacy_owner_role',$18,true)
            """, connection, transaction);
        var values = new[]
        {
            options.ContractSha256,
            options.Contract.DeploymentId,
            options.Contract.SystemIdentifierSha256,
            options.Contract.Prefix,
            options.Contract.Owner.Name,
            options.Contract.MigratorCapability.Name,
            options.Contract.ApiCapability.Name,
            options.Contract.IngestionCapability.Name,
            options.Contract.CalendarImporterCapability.Name,
            options.Contract.ExporterCapability.Name,
            options.Contract.AuditCapability.Name,
            options.Contract.TimescaleScheduler.Name,
            options.ExpectedLogin,
            options.LoginVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.TimescaleVersion,
            options.UuidOsspVersion,
            options.LegacyPrivilegeCutover ? "on" : "off",
            legacyOwner,
        };
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ExporterRoleStatus> ValidateLegacyOptionalStepAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var status = await ReadExporterRoleStatusAsync(connection, cancellationToken);
        if (status == ExporterRoleStatus.Incomplete)
        {
            throw new MigratorRejectedException("legacy_exporter_role_ambiguous");
        }
        return status;
    }

    private static async Task<ExporterRoleStatus> ReadExporterRoleStatusAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        await ReadExporterRoleStatusAsync(connection, transaction: null, cancellationToken);

    private static async Task<ExporterRoleStatus> ReadExporterRoleStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT CASE
                WHEN NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='saydin_exporter') THEN 0
                WHEN pg_has_role('saydin_exporter', 'pg_monitor', 'member') THEN 2
                ELSE 1
            END
            """, connection, transaction);
        return (ExporterRoleStatus)Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task SetControlStateAsync(
        NpgsqlConnection connection,
        string state,
        string manifestChecksum,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public.saydin_migration_control
            SET state=$1, manifest_checksum=$2, last_error_code=$3, updated_at=now()
            WHERE singleton=1
            """, connection);
        command.Parameters.AddWithValue(state);
        command.Parameters.AddWithValue(manifestChecksum);
        command.Parameters.AddWithValue((object?)errorCode ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new MigratorRejectedException("migration_control_row_missing");
    }

    private static async Task TrySetControlFailedAsync(
        NpgsqlConnection connection,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            return;
        try
        {
            if (!await ScalarAsync<bool>(connection,
                    "SELECT to_regclass('public.saydin_migration_control') IS NOT NULL",
                    cancellationToken))
                return;
            await using var command = new NpgsqlCommand("""
                UPDATE public.saydin_migration_control
                SET state='failed', last_error_code=$1, updated_at=now()
                WHERE singleton=1
                """, connection);
            command.Parameters.AddWithValue(errorCode);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Original error remains authoritative.
        }
    }

    private static async Task<TargetIdentity> ReadTargetIdentityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT current_database(), current_user,
                   COALESCE(inet_server_addr()::text, 'local'),
                   COALESCE(inet_server_port(), 0),
                   system_identifier::text
            FROM pg_control_system()
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("database_identity_unavailable");
        return new TargetIdentity(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetString(4));
    }

    private static async Task AssertTargetIdentityAsync(
        NpgsqlConnection connection,
        TargetIdentity expected,
        CancellationToken cancellationToken)
    {
        var actual = await ReadTargetIdentityAsync(connection, cancellationToken);
        if (actual != expected)
            throw new MigratorRejectedException("database_target_changed");
    }

    private static async Task<List<LegacyRow>> ReadLegacyRowsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<LegacyRow>();
        await using var command = new NpgsqlCommand(
            "SELECT version, checksum FROM public.schema_migrations ORDER BY version COLLATE \"C\"", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new LegacyRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    private static async Task<List<MigrationStateRow>> ReadMigrationStatesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<MigrationStateRow>();
        await using var command = new NpgsqlCommand(
            "SELECT version, checksum, state FROM public.schema_migrations ORDER BY version COLLATE \"C\"", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new MigrationStateRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2)));
        return rows;
    }

    private static async Task<MigrationStateRow?> ReadMigrationStateAsync(
        NpgsqlConnection connection,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT version, checksum, state FROM public.schema_migrations WHERE version=$1", connection);
        command.Parameters.AddWithValue(version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MigrationStateRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2))
            : null;
    }

    private static async Task<bool> IsMigrationTerminalAsync(
        NpgsqlConnection connection,
        string version,
        CancellationToken cancellationToken) =>
        await ReadMigrationStateAsync(connection, version, cancellationToken) is
        { State: "succeeded" or "skipped_optional" };

    private static async Task<ControlRow?> ReadControlAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT control_version, state FROM public.saydin_migration_control WHERE singleton=1", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ControlRow(reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    private static void EnsureChecksumMatches(MigrationDefinition migration, MigrationStateRow row)
    {
        if (!string.Equals(row.Checksum, migration.Checksum, StringComparison.Ordinal))
            throw new MigratorRejectedException("migration_checksum_mismatch", migration.Version);
    }

    internal static void ValidateTrustedPrefix(MigrationManifest manifest)
    {
        var trustedCount = MigratorMigrationTrustRoot.Versions.Count;
        if (manifest.Migrations.Count < trustedCount ||
            !manifest.Migrations.Take(trustedCount).Select(migration => migration.Version)
                .SequenceEqual(MigratorMigrationTrustRoot.Versions, StringComparer.Ordinal))
            throw new MigratorRejectedException("historical_manifest_mismatch");
        foreach (var migration in manifest.Migrations.Take(trustedCount))
        {
            if (!MigratorMigrationTrustRoot.Checksums.TryGetValue(migration.Version, out var expected) ||
                !string.Equals(migration.Checksum, expected, StringComparison.Ordinal))
                throw new MigratorRejectedException(
                    LegacyVersions.Contains(migration.Version, StringComparer.Ordinal)
                        ? "historical_checksum_mismatch"
                        : "pinned_checksum_mismatch",
                    migration.Version);
        }
        var tail = manifest.Migrations.Skip(trustedCount).ToArray();
        if (tail.Any(migration => migration.Kind != MigrationKind.Sql) ||
            tail.Select(migration => migration.Version).Distinct(StringComparer.Ordinal).Count() != tail.Length ||
            tail.Length > 0 && string.CompareOrdinal(
                tail[0].Version, MigratorMigrationTrustRoot.Versions[^1]) <= 0)
            throw new MigratorRejectedException("future_migration_tail_invalid");
    }

    private static void ValidateCanonicalPrefixFixture(MigrationManifest manifest)
    {
        var trustedPrefixCount = Math.Min(
            manifest.Migrations.Count, MigratorMigrationTrustRoot.Versions.Count);
        if (manifest.Migrations.Count < LegacyVersions.Length ||
            !manifest.Migrations.Take(trustedPrefixCount).Select(migration => migration.Version)
                .SequenceEqual(MigratorMigrationTrustRoot.Versions.Take(trustedPrefixCount),
                    StringComparer.Ordinal))
            throw new MigratorRejectedException("fixture_manifest_not_canonical_prefix");
        foreach (var migration in manifest.Migrations.Take(trustedPrefixCount))
        {
            if (!MigratorMigrationTrustRoot.Checksums.TryGetValue(migration.Version, out var expected) ||
                !string.Equals(migration.Checksum, expected, StringComparison.Ordinal))
                throw new MigratorRejectedException("fixture_checksum_mismatch", migration.Version);
        }
    }

    private static async Task SetDeterministicSearchPathAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('search_path','pg_catalog,public,pg_temp',false)", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        MigratorRejectedException rejected => rejected.Code,
        PostgresException postgres => $"postgres_{postgres.SqlState}",
        NpgsqlException => "npgsql_failure",
        TimeoutException => "timeout",
        _ => exception.GetType().Name,
    };

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken) =>
        await ScalarAsync<T>(connection, sql, transaction: null, cancellationToken);

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            throw new MigratorRejectedException("database_scalar_missing");
        return (T)value;
    }

    private enum DatabaseState
    {
        Blank,
        LegacyComplete014,
        Managed,
        Ambiguous,
    }

    private enum ExporterRoleStatus
    {
        Absent = 0,
        Incomplete = 1,
        Complete = 2,
    }

    private sealed record LegacyRow(string Version, string? Checksum);
    private sealed record MigrationStateRow(string Version, string? Checksum, string State);
    private sealed record ControlRow(int ControlVersion, string State);
    private sealed record TargetIdentity(
        string Database,
        string User,
        string ServerAddress,
        int ServerPort,
        string SystemIdentifier);
}
