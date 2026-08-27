using System.Data;
using System.Globalization;
using System.Text;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DataQualityAudit;

internal sealed class AuditRunner(
    NpgsqlDataSource dataSource,
    VerifiedAuditInput input,
    EmbeddedMigrationManifest embeddedMigrations,
    byte[] hmacKey,
    RoleContract roleContract,
    DateTimeOffset backupV1ValidUntilUtc)
{
    internal static IReadOnlyList<string> RequiredTableNames { get; } = Array.AsReadOnly(new[]
    {
        "saydin_migration_control",
        "schema_migrations",
        "assets",
        "price_points",
        "inflation_rates",
        "ingestion_windows",
        "ingestion_jobs",
        "users",
        "saved_scenarios",
        "market_holidays",
        "activity_logs",
        "market_calendars",
        "market_calendar_releases",
        "market_calendar_release_sources",
        "market_calendar_days",
        "market_calendar_active_releases",
        "asset_market_calendars",
        "saydin_role_contract",
        "provider_fetch_payloads",
        "price_observation_attributions",
        "inflation_observation_attributions",
        "installation_credentials",
        "asset_catalog_state",
    });

    private readonly AuditBudget _budget = input.Manifest.Budget;
    private readonly AuditAccumulator _findings = new(hmacKey, input.Manifest.Budget.MaxEvidencePerCheck);

    private sealed record BackupRoleRow(
        string Name,
        bool CanLogin,
        bool Superuser,
        bool CreateDatabase,
        bool CreateRole,
        bool Inherit,
        bool Replication,
        bool BypassRls,
        int ConnectionLimit,
        string? ValidUntilUtc,
        bool ConfigIsNull,
        string? Marker);

    public async Task<EvidenceContent> RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await ConfigureReadOnlyTransactionAsync(connection, transaction, cancellationToken);

        var targetIdentity = await RunPreflightAsync(connection, transaction, cancellationToken);
        EnsureChecks();

        var laneWindows = new Dictionary<AuditLane, IReadOnlyList<DatabaseWindow>>();
        foreach (var lane in input.Manifest.Scope.Lanes
                     .OrderBy(lane => lane.Source, StringComparer.Ordinal)
                     .ThenBy(lane => lane.AssetId)
                     .ThenBy(lane => lane.JobType, StringComparer.Ordinal)
                     .ThenBy(lane => lane.From))
        {
            var windows = await LoadWindowsAsync(connection, transaction, lane, cancellationToken);
            laneWindows.Add(lane, windows);
            AuditLedgerContinuity(lane, windows);
            await AuditWindowCompletenessAsync(connection, transaction, lane, windows, cancellationToken);
            await AuditFinancialInvariantsAsync(connection, transaction, lane, cancellationToken);
            await AuditProvenanceAsync(connection, transaction, lane, cancellationToken);
            await AuditObservationAuthorityAsync(connection, transaction, lane, cancellationToken);
            await AuditJobWindowStateAsync(connection, transaction, lane, cancellationToken);
            await AuditUnattestedWritesAsync(connection, transaction, lane, cancellationToken);
        }

        await AuditConstraintDriftAndDuplicatesAsync(connection, transaction, cancellationToken);
        await AuditFetchLedgerAsync(connection, transaction, laneWindows, cancellationToken);
        await AuditCalendarAsync(connection, transaction, laneWindows, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);

        var checks = _findings.Build();
        return new EvidenceContent(
            2,
            "DQ-001..009/v2",
            embeddedMigrations.Checksum,
            input.CanonicalSha256,
            targetIdentity,
            checks,
            _findings.BuildRecommendations());
    }

    private void EnsureChecks()
    {
        _findings.Ensure("DQ-001", AuditSeverity.Critical);
        _findings.Ensure("DQ-002", AuditSeverity.Critical);
        _findings.Ensure("DQ-003", AuditSeverity.Critical);
        _findings.Ensure("DQ-004", AuditSeverity.High);
        _findings.Ensure("DQ-005", AuditSeverity.High);
        _findings.Ensure("DQ-006", AuditSeverity.Critical);
        _findings.Ensure("DQ-007", AuditSeverity.Critical);
        _findings.Ensure("DQ-008", AuditSeverity.Critical);
        _findings.Ensure("DQ-009", AuditSeverity.High);
    }

    private async Task ConfigureReadOnlyTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync("SET TRANSACTION READ ONLY");
        await ExecuteAsync("SELECT set_config('application_name', 'saydin-data-quality-audit', true)");
        await ExecuteAsync("SELECT set_config('statement_timeout', $1, true)",
            $"{_budget.StatementTimeoutMilliseconds}ms");
        await ExecuteAsync("SELECT set_config('lock_timeout', $1, true)",
            $"{_budget.LockTimeoutMilliseconds}ms");
        await ExecuteAsync("SELECT set_config('idle_in_transaction_session_timeout', '60s', true)");
        return;

        async Task ExecuteAsync(string sql, object? value = null)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            if (value is not null)
                command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<string> RunPreflightAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await EnsureRequiredObjectsAsync(connection, transaction, cancellationToken);
        var database = await ScalarAsync<string>(connection, transaction,
            "SELECT current_database()", cancellationToken);
        var readOnly = await ScalarAsync<string>(connection, transaction,
            "SHOW transaction_read_only", cancellationToken);
        if (!string.Equals(database, input.Manifest.Target.Database, StringComparison.Ordinal) ||
            !string.Equals(readOnly, "on", StringComparison.Ordinal))
            throw new AuditRejectedException("target_or_read_only_mismatch", AuditExitCodes.PreflightRejected);

        var systemIdentifier = await ScalarAsync<string>(connection, transaction,
            "SELECT system_identifier::text FROM pg_control_system()", cancellationToken);
        var systemIdentifierHash = AuditCryptography.Sha256Hex(Encoding.UTF8.GetBytes(systemIdentifier));
        if (!CryptographicEquals(systemIdentifierHash, input.Manifest.Target.SystemIdentifierSha256))
            throw new AuditRejectedException("target_system_identifier_mismatch", AuditExitCodes.PreflightRejected);

        await VerifyMigrationControlAsync(connection, transaction, cancellationToken);
        await VerifyReadOnlyRoleAsync(connection, transaction, cancellationToken);
        await VerifyBudgetsAsync(connection, transaction, cancellationToken);
        return AuditCryptography.Sha256Hex(
            Encoding.UTF8.GetBytes($"{database}\0{systemIdentifierHash}"));
    }

    private static async Task EnsureRequiredObjectsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT count(*)
            FROM unnest($1::text[]) AS required(name)
            WHERE to_regclass('public.' || required.name) IS NULL
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(RequiredTableNames.ToArray());
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            throw new AuditRejectedException("required_schema_object_missing", AuditExitCodes.PreflightRejected);
    }

    private async Task VerifyMigrationControlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var control = new NpgsqlCommand("""
            SELECT state, manifest_checksum
            FROM public.saydin_migration_control
            WHERE singleton = 1
            """, connection, transaction))
        await using (var reader = await control.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                !string.Equals(reader.GetString(0), "ready", StringComparison.Ordinal) ||
                !CryptographicEquals(reader.GetString(1), embeddedMigrations.Checksum) ||
                await reader.ReadAsync(cancellationToken))
                throw new AuditRejectedException("migration_control_not_ready", AuditExitCodes.PreflightRejected);
        }

        var rows = new Dictionary<string, (string? Checksum, string State)>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""
            SELECT version, checksum, state
            FROM public.schema_migrations
            ORDER BY version COLLATE "C"
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(reader.GetString(0),
                    (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }

        if (rows.Count != embeddedMigrations.Migrations.Count)
            throw new AuditRejectedException("migration_set_mismatch", AuditExitCodes.PreflightRejected);
        foreach (var migration in embeddedMigrations.Migrations)
        {
            if (!rows.TryGetValue(migration.Version, out var row) ||
                !CryptographicEquals(row.Checksum ?? string.Empty, migration.Checksum) ||
                (row.State != "succeeded" &&
                 !(migration.Version == "012b_create_exporter_role" && row.State == "skipped_optional")))
                throw new AuditRejectedException("migration_checksum_or_state_mismatch", AuditExitCodes.PreflightRejected);
        }
    }

    private static async Task VerifyReadOnlyRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH audited_tables(name) AS (
                SELECT unnest($1::text[])
            )
            SELECT role.rolsuper OR role.rolcreaterole OR role.rolcreatedb
                       OR role.rolreplication OR role.rolbypassrls
                   OR has_database_privilege(current_user, current_database(), 'TEMP')
                   OR has_schema_privilege(current_user, 'public', 'CREATE')
                   OR NOT has_function_privilege(
                       current_user, 'pg_catalog.pg_control_system()', 'EXECUTE')
                   OR NOT has_function_privilege(
                       current_user, 'public.verify_market_calendar_release_payload(uuid)', 'EXECUTE')
                   OR has_function_privilege(
                       current_user, 'public.seal_market_calendar_release(uuid)', 'EXECUTE')
                   OR has_function_privilege(
                       current_user, 'public.activate_market_calendar_release(text,uuid,uuid)', 'EXECUTE')
                   OR EXISTS (
                       SELECT 1 FROM audited_tables table_name
                       JOIN pg_class relation
                         ON relation.oid = to_regclass('public.' || table_name.name)
                       WHERE pg_get_userbyid(relation.relowner) = current_user
                          OR has_table_privilege(current_user, relation.oid, 'INSERT')
                          OR has_table_privilege(current_user, relation.oid, 'UPDATE')
                          OR has_table_privilege(current_user, relation.oid, 'DELETE')
                          OR has_table_privilege(current_user, relation.oid, 'TRUNCATE')) AS unsafe
            FROM pg_roles role
            WHERE role.rolname = current_user
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(RequiredTableNames.ToArray());
        if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
            throw new AuditRejectedException("audit_role_is_writable", AuditExitCodes.PreflightRejected);
    }

    private async Task VerifyBudgetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pg_database_size(current_database()),
                   (SELECT max(pg_total_relation_size(to_regclass('public.' || name)))
                      FROM unnest($1::text[]) required(name))
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(RequiredTableNames.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new AuditRejectedException("headroom_probe_failed", AuditExitCodes.BudgetRejected);
        var databaseBytes = reader.GetInt64(0);
        var relationBytes = reader.GetInt64(1);
        if (databaseBytes > _budget.MaxDatabaseBytes || relationBytes > _budget.MaxRelationBytes ||
            _budget.AttestedHeadroomBytes < _budget.MaxEvidenceBytes * 2)
            throw new AuditRejectedException("query_budget_exceeded", AuditExitCodes.BudgetRejected);
    }

    private async Task<IReadOnlyList<DatabaseWindow>> LoadWindowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditLane lane,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, source, asset_id, job_type, range_start, range_end,
                   contract_version, state, requested_calendar_count,
                   expected_observation_count, accepted_distinct_count,
                   rejected_count, expected_no_data_count, calendar_release_id
            FROM public.ingestion_windows
            WHERE source = $1
              AND asset_id IS NOT DISTINCT FROM $2::uuid
              AND job_type = $3
              AND contract_version = $4
              AND range_end >= $5
              AND range_start <= $6
            ORDER BY range_start, range_end, id
            LIMIT $7
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(lane.Source);
        command.Parameters.AddWithValue((object?)lane.AssetId ?? DBNull.Value);
        command.Parameters.AddWithValue(lane.JobType);
        command.Parameters.AddWithValue(lane.ContractVersion);
        command.Parameters.AddWithValue(lane.From);
        command.Parameters.AddWithValue(lane.Through);
        command.Parameters.AddWithValue(_budget.MaxWindows + 1);
        var windows = new List<DatabaseWindow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            windows.Add(new DatabaseWindow(
                reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4), reader.GetFieldValue<DateOnly>(5),
                reader.GetInt32(6), reader.GetString(7), reader.GetInt32(8),
                reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetInt32(12), reader.IsDBNull(13) ? null : reader.GetGuid(13)));
        }

        if (windows.Count > _budget.MaxWindows)
            throw new AuditRejectedException("window_budget_exceeded", AuditExitCodes.BudgetRejected);
        return windows;
    }

    private void AuditLedgerContinuity(AuditLane lane, IReadOnlyList<DatabaseWindow> windows)
    {
        foreach (var violation in LedgerContinuity.Analyze(lane, windows))
            _findings.Add("DQ-002", AuditSeverity.Critical,
                violation.ViolationCode, violation.BusinessKey);
    }

    private async Task AuditWindowCompletenessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditLane lane,
        IReadOnlyList<DatabaseWindow> windows,
        CancellationToken cancellationToken)
    {
        foreach (var window in windows)
        {
            if (window.State is not ("succeeded" or "expected_no_data"))
            {
                _findings.Add("DQ-001", AuditSeverity.Critical,
                    $"window_not_terminal:{window.State}",
                    $"{LaneKey(lane)}|{window.Id:D}|{window.RangeStart:yyyy-MM-dd}|{window.RangeEnd:yyyy-MM-dd}");
                continue;
            }

            var violations = lane.Source == "evds"
                ? await QueryWindowViolationsAsync(connection, transaction, window,
                    lane, AuditSql.InflationWindowCompleteness, cancellationToken)
                : await QueryWindowViolationsAsync(connection, transaction, window,
                    lane, AuditSql.PriceWindowCompleteness, cancellationToken);
            AddBatch("DQ-001", AuditSeverity.Critical, violations);
        }
    }

    private async Task AuditFinancialInvariantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditLane lane,
        CancellationToken cancellationToken)
    {
        var batch = lane.Source == "evds"
            ? await QueryViolationsAsync(connection, transaction, AuditSql.InflationInvariants,
                command => AddLaneRange(command, lane), cancellationToken)
            : await QueryViolationsAsync(connection, transaction, AuditSql.PriceInvariants,
                command =>
                {
                    command.Parameters.AddWithValue(lane.AssetId!.Value);
                    command.Parameters.AddWithValue(lane.From);
                    command.Parameters.AddWithValue(lane.Through);
                }, cancellationToken);
        AddBatch("DQ-004", AuditSeverity.High, batch);
    }

    private async Task AuditProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditLane lane,
        CancellationToken cancellationToken)
    {
        QueryBatch batch;
        if (lane.Source == "evds")
        {
            batch = await QueryViolationsAsync(connection, transaction, AuditSql.InflationProvenance,
                command => AddLaneRange(command, lane), cancellationToken);
        }
        else
        {
            batch = await QueryViolationsAsync(connection, transaction, AuditSql.PriceProvenance,
                command =>
                {
                    command.Parameters.AddWithValue(lane.AssetId!.Value);
                    command.Parameters.AddWithValue(lane.From);
                    command.Parameters.AddWithValue(lane.Through);
                }, cancellationToken);
        }
        AddBatch("DQ-005", AuditSeverity.High, batch);
    }

    private async Task AuditObservationAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditLane lane,
        CancellationToken cancellationToken)
    {
        var authoritySql = lane.Source == "evds"
            ? AuditSql.InflationAuthority
            : AuditSql.PriceAuthority;
        var legacySql = lane.Source == "evds"
            ? AuditSql.InflationLegacyAuthority
            : AuditSql.PriceLegacyAuthority;

        void ConfigureLane(NpgsqlCommand command)
        {
            if (lane.Source != "evds") command.Parameters.AddWithValue(lane.AssetId!.Value);
            command.Parameters.AddWithValue(lane.From);
            command.Parameters.AddWithValue(lane.Through);
        }

        var authority = await QueryViolationsAsync(
            connection, transaction, authoritySql, ConfigureLane, cancellationToken);
        AddBatch("DQ-009", AuditSeverity.High, authority);

        // Migration 020 intentionally permits the complete all-null tuple for
        // pre-authority history. It is structurally valid, but its provenance is
        // unknown and therefore remains a High data-quality finding. The main
        // authority queries exclude the all-null cardinality, so this second batch
        // is disjoint and cannot double-count partial or authority-aware rows.
        var legacy = await QueryViolationsAsync(
            connection, transaction, legacySql, ConfigureLane, cancellationToken);
        AddBatch("DQ-009", AuditSeverity.High, legacy);
    }

    private async Task AuditFetchLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyDictionary<AuditLane, IReadOnlyList<DatabaseWindow>> laneWindows,
        CancellationToken cancellationToken)
    {
        var windowIds = laneWindows.Values.SelectMany(value => value)
            .Select(window => window.Id).Distinct().ToArray();
        var sources = input.Manifest.Scope.Lanes
            .Select(lane => lane.Source).Distinct(StringComparer.Ordinal).ToArray();
        await using (var cardinality = new NpgsqlCommand("""
            SELECT count(*) FROM (
              SELECT 1 FROM public.provider_fetch_payloads payload
               WHERE payload.provider_source=ANY($1::text[])
                 AND payload.first_observed_at BETWEEN $2 AND $3
              UNION ALL
              SELECT 1 FROM public.price_observation_attributions
               WHERE ingestion_window_id=ANY($4::uuid[])
              UNION ALL
              SELECT 1 FROM public.inflation_observation_attributions
               WHERE ingestion_window_id=ANY($4::uuid[])
              LIMIT $5) bounded
            """, connection, transaction))
        {
            cardinality.Parameters.AddWithValue(sources);
            cardinality.Parameters.AddWithValue(input.Manifest.Scope.LegacyGraceEndedAtUtc);
            cardinality.Parameters.AddWithValue(input.Manifest.Scope.AsOfUtc);
            cardinality.Parameters.AddWithValue(windowIds);
            cardinality.Parameters.AddWithValue(_budget.MaxGlobalRows + 1);
            if (Convert.ToInt64(await cardinality.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) > _budget.MaxGlobalRows)
                throw new AuditRejectedException(
                    "global_scan_budget_exceeded", AuditExitCodes.BudgetRejected);
        }
        var batch = await QueryViolationsAsync(connection, transaction, AuditSql.FetchLedger,
            command =>
            {
                command.Parameters.AddWithValue(windowIds);
                command.Parameters.AddWithValue(sources);
                command.Parameters.AddWithValue(input.Manifest.Scope.LegacyGraceEndedAtUtc);
                command.Parameters.AddWithValue(input.Manifest.Scope.AsOfUtc);
                command.Parameters.AddWithValue(_budget.MaxGlobalRows);
            }, cancellationToken);
        if (batch.TotalCount > _budget.MaxGlobalRows)
            throw new AuditRejectedException(
                "global_scan_budget_exceeded", AuditExitCodes.BudgetRejected);
        AddBatch("DQ-009", AuditSeverity.High, batch);
    }

    private async Task AuditJobWindowStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditLane lane,
        CancellationToken cancellationToken)
    {
        var batch = await QueryViolationsAsync(connection, transaction, AuditSql.JobWindowState,
            command =>
            {
                command.Parameters.AddWithValue(lane.Source);
                command.Parameters.AddWithValue((object?)lane.AssetId ?? DBNull.Value);
                command.Parameters.AddWithValue(lane.JobType);
                command.Parameters.AddWithValue(lane.ContractVersion);
                command.Parameters.AddWithValue(lane.From);
                command.Parameters.AddWithValue(lane.Through);
            }, cancellationToken);
        AddBatch("DQ-007", AuditSeverity.Critical, batch);
    }

    private async Task AuditUnattestedWritesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditLane lane,
        CancellationToken cancellationToken)
    {
        var sql = lane.Source == "evds" ? AuditSql.UnattestedInflation : AuditSql.UnattestedPrice;
        var batch = await QueryViolationsAsync(connection, transaction, sql,
            command =>
            {
                if (lane.Source != "evds")
                    command.Parameters.AddWithValue(lane.AssetId!.Value);
                command.Parameters.AddWithValue(lane.From);
                command.Parameters.AddWithValue(lane.Through);
                command.Parameters.AddWithValue(input.Manifest.Scope.LegacyGraceEndedAtUtc);
            }, cancellationToken);
        AddBatch("DQ-008", AuditSeverity.Critical, batch);

        var legacyJobs = await QueryViolationsAsync(connection, transaction, AuditSql.LegacyJobs,
            command =>
            {
                command.Parameters.AddWithValue(lane.Source);
                command.Parameters.AddWithValue((object?)lane.AssetId ?? DBNull.Value);
                command.Parameters.AddWithValue(lane.JobType);
                command.Parameters.AddWithValue(lane.From);
                command.Parameters.AddWithValue(lane.Through);
                command.Parameters.AddWithValue(input.Manifest.Scope.LegacyGraceEndedAtUtc);
            }, cancellationToken);
        AddBatch("DQ-008", AuditSeverity.Critical, legacyJobs);
    }

    private async Task AuditConstraintDriftAndDuplicatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var fingerprint = new NpgsqlCommand("""
            SELECT
              EXISTS (SELECT 1 FROM pg_constraint c
                WHERE c.conname='pk_price_points'
                  AND c.conrelid='public.price_points'::regclass AND c.contype='p' AND c.convalidated
                  AND c.conkey=ARRAY[
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='asset_id'),
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='price_date')
                  ]::smallint[]),
              EXISTS (SELECT 1 FROM pg_constraint c
                WHERE c.conname='pk_inflation_rates'
                  AND c.conrelid='public.inflation_rates'::regclass AND c.contype='p' AND c.convalidated
                  AND c.conkey=ARRAY[
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='period_date'),
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='source')
                  ]::smallint[]),
              EXISTS (SELECT 1 FROM pg_constraint c JOIN pg_index i ON i.indexrelid=c.conindid
                WHERE c.conname='uq_ingestion_windows_logical'
                  AND c.conrelid='public.ingestion_windows'::regclass AND c.contype='u'
                  AND c.convalidated AND i.indisunique AND i.indnullsnotdistinct
                  AND c.conkey=ARRAY[
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='source'),
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='asset_id'),
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='job_type'),
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='range_start'),
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='range_end'),
                    (SELECT attnum FROM pg_attribute WHERE attrelid=c.conrelid AND attname='contract_version')
                  ]::smallint[])
            """, connection, transaction);
        await using var fingerprintReader = await fingerprint.ExecuteReaderAsync(cancellationToken);
        if (!await fingerprintReader.ReadAsync(cancellationToken))
            throw new AuditRejectedException("constraint_fingerprint_probe_failed", AuditExitCodes.RuntimeFailure);
        var priceOk = fingerprintReader.GetBoolean(0);
        var inflationOk = fingerprintReader.GetBoolean(1);
        var windowsOk = fingerprintReader.GetBoolean(2);
        await fingerprintReader.DisposeAsync();
        if (!priceOk)
            _findings.Add("DQ-003", AuditSeverity.Critical, "price_primary_key_drift", "price_points");
        if (!inflationOk)
            _findings.Add("DQ-003", AuditSeverity.Critical, "inflation_primary_key_drift", "inflation_rates");
        if (!windowsOk)
            _findings.Add("DQ-003", AuditSeverity.Critical, "window_logical_unique_drift", "ingestion_windows");

        if (!priceOk)
        {
            foreach (var lane in input.Manifest.Scope.Lanes.Where(lane => lane.AssetId is not null))
            {
                var batch = await QueryViolationsAsync(connection, transaction, AuditSql.PriceDuplicates,
                    command =>
                    {
                        command.Parameters.AddWithValue(lane.AssetId!.Value);
                        command.Parameters.AddWithValue(lane.From);
                        command.Parameters.AddWithValue(lane.Through);
                    }, cancellationToken);
                AddBatch("DQ-003", AuditSeverity.Critical, batch);
            }
        }
        if (!inflationOk)
        {
            foreach (var lane in input.Manifest.Scope.Lanes.Where(lane => lane.Source == "evds"))
            {
                var batch = await QueryViolationsAsync(connection, transaction, AuditSql.InflationDuplicates,
                    command => AddLaneRange(command, lane), cancellationToken);
                AddBatch("DQ-003", AuditSeverity.Critical, batch);
            }
        }
        if (!windowsOk)
        {
            var batch = await QueryViolationsAsync(connection, transaction, AuditSql.WindowDuplicates,
                _ => { }, cancellationToken);
            AddBatch("DQ-003", AuditSeverity.Critical, batch);
        }

        await AuditFenceTriggersAsync(connection, transaction, cancellationToken);
        await AuditPriceAuthorityStructureAsync(connection, transaction, cancellationToken);
        await AuditApiTrustStructureAsync(connection, transaction, cancellationToken);
        await AuditPrincipalRetentionStructureAsync(connection, transaction, cancellationToken);
        await AuditBackupIdentityAsync(connection, transaction, cancellationToken);
    }

    private async Task AuditBackupIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<BackupRoleRow>();
        await using (var roles = new NpgsqlCommand("""
            SELECT role.rolname,role.rolcanlogin,role.rolsuper,role.rolcreatedb,
                   role.rolcreaterole,role.rolinherit,role.rolreplication,
                   role.rolbypassrls,role.rolconnlimit,
                   CASE WHEN role.rolvaliduntil IS NULL THEN NULL ELSE
                       pg_catalog.to_char(role.rolvaliduntil AT TIME ZONE 'UTC',
                           'YYYY-MM-DD"T"HH24:MI:SS"Z"') END,
                   role.rolconfig IS NULL,
                   pg_catalog.shobj_description(role.oid,'pg_authid')
              FROM pg_catalog.pg_roles role
             WHERE pg_catalog.left(role.rolname,pg_catalog.length($1)+14)=
                   $1||'_backup_login_'
             ORDER BY role.rolname COLLATE "C"
            """, connection, transaction))
        {
            roles.Parameters.AddWithValue(roleContract.Prefix);
            await using var reader = await roles.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new BackupRoleRow(
                    reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2),
                    reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.GetBoolean(6), reader.GetBoolean(7), reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetBoolean(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        var parsed = new List<ManagedRole>();
        var expectedV1 = roleContract.BackupLogin(1, backupV1ValidUntilUtc);
        foreach (var row in rows)
        {
            if (row.Marker is null || !roleContract.TryResolveManagedMarker(row.Marker, out var role) ||
                role.Purpose != "backup" || !string.Equals(role.Name, row.Name, StringComparison.Ordinal) ||
                row.CanLogin != true || row.Superuser || row.CreateDatabase || row.CreateRole || row.Inherit ||
                !row.Replication || row.BypassRls || row.ConnectionLimit != 2 || !row.ConfigIsNull ||
                !string.Equals(row.ValidUntilUtc,
                    RoleContract.FormatBackupValidUntil(role.ValidUntilUtc!.Value),
                    StringComparison.Ordinal))
            {
                _findings.Add("DQ-003", AuditSeverity.Critical,
                    "backup_role_contract_drift", row.Name);
                continue;
            }
            parsed.Add(role);
        }
        if (parsed.Count is < 1 or > 2 || parsed.All(role => role.LoginVersion != 1) ||
            parsed.Select(role => role.LoginVersion).Distinct().Count() != parsed.Count ||
            !parsed.Any(role => role.LoginVersion == 1 &&
                string.Equals(role.Marker, expectedV1.Marker, StringComparison.Ordinal)))
            _findings.Add("DQ-003", AuditSeverity.Critical,
                "backup_role_version_set_drift", roleContract.Prefix);

        var epoch = await ScalarAsync<long>(connection, transaction,
            "SELECT extract(epoch FROM pg_catalog.clock_timestamp())::bigint", cancellationToken);
        var now = DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (parsed.Count == 0 || parsed.All(role => role.ValidUntilUtc!.Value < now.AddHours(24)))
            _findings.Add("DQ-003", AuditSeverity.Critical,
                "backup_rotation_horizon_insufficient", roleContract.Prefix);

        foreach (var backup in parsed)
        {
            await using var command = new NpgsqlCommand("""
                WITH backup AS (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=$1),
                direct_grant_or_ownership AS (
                    SELECT 1 FROM pg_catalog.pg_database object, backup
                     WHERE object.datdba=backup.oid OR EXISTS (
                         SELECT 1 FROM pg_catalog.aclexplode(object.datacl) acl
                          WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_namespace object, backup
                     WHERE object.nspowner=backup.oid OR EXISTS (
                         SELECT 1 FROM pg_catalog.aclexplode(object.nspacl) acl
                          WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_class object, backup
                     WHERE object.relowner=backup.oid OR EXISTS (
                         SELECT 1 FROM pg_catalog.aclexplode(object.relacl) acl
                          WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_attribute object
                      CROSS JOIN backup
                     WHERE object.attnum>0 AND NOT object.attisdropped AND EXISTS (
                         SELECT 1 FROM pg_catalog.aclexplode(object.attacl) acl
                          WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_proc object, backup
                     WHERE object.proowner=backup.oid OR EXISTS (
                         SELECT 1 FROM pg_catalog.aclexplode(object.proacl) acl
                          WHERE acl.grantee=backup.oid)
                    UNION ALL
                    SELECT 1 FROM pg_catalog.pg_default_acl object, backup
                     WHERE object.defaclrole=backup.oid OR EXISTS (
                         SELECT 1 FROM pg_catalog.aclexplode(object.defaclacl) acl
                          WHERE acl.grantee=backup.oid)
                ), memberships AS (
                    SELECT 1 FROM pg_catalog.pg_auth_members membership, backup
                     WHERE membership.roleid=backup.oid OR membership.member=backup.oid
                )
                SELECT NOT pg_catalog.has_database_privilege($1,current_database(),'CONNECT')
                   AND NOT pg_catalog.has_database_privilege($1,current_database(),'CREATE')
                   AND NOT pg_catalog.has_database_privilege($1,current_database(),'TEMPORARY')
                   AND NOT pg_catalog.has_schema_privilege($1,'public','USAGE')
                   AND NOT pg_catalog.has_schema_privilege($1,'public','CREATE')
                   AND NOT pg_catalog.has_function_privilege(
                       $1,'pg_catalog.pg_control_system()','EXECUTE')
                   AND NOT EXISTS (SELECT 1 FROM direct_grant_or_ownership)
                   AND NOT EXISTS (SELECT 1 FROM memberships)
                """, connection, transaction);
            command.Parameters.AddWithValue(backup.Name);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
                _findings.Add("DQ-003", AuditSeverity.Critical,
                    "backup_role_acl_or_membership_drift", backup.Name);
        }
    }

    private async Task AuditPriceAuthorityStructureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH expected_constraints(name,relation_name,kind,definition_sha256,validated,delete_action) AS (VALUES
              ('chk_price_points_authority_tuple','price_points','c','56d37a4074f20e538a6a32bfef3dba6271160b82544b672aa2bbe12e744bf3e5',false,NULL),
              ('chk_price_points_provider_kind','price_points','c','15dbb9012ff5cb5411e43c4abad1790214399ed5dad677715f0c2b8350feae5a',false,NULL),
              ('chk_price_points_numeric','price_points','c','dca4038f346c80c13292b92135b6f1640b68a57ba2589e4925a7d26b5e556f57',false,NULL),
              ('chk_price_points_provider_shape','price_points','c','56e7e2dfd5a083c5cb3eb14e1765b6bf7256e1d2a16d1705b52f82f2b94d6af0',false,NULL),
              ('chk_price_points_as_of','price_points','c','c9191bfeb8179e38f6376eeb79817d89d5d69e99c755d86680fd83122c434268',false,NULL),
              ('chk_inflation_rates_authority_tuple','inflation_rates','c','5cc232a700955c8093c1c7b376391000d08bbb80353a5b24fb51ffe2da8609fb',false,NULL),
              ('chk_inflation_rates_numeric','inflation_rates','c','995968bcc5179b2e8f517449d3f678d995e6404ed6eb2bbafe4414ef21cf8291',false,NULL),
              ('chk_inflation_rates_as_of','inflation_rates','c','6550f93376e8d144fadd75f8a7a3c2d647c2e530950ef6e3192c9cecf97b4b09',false,NULL),
              ('pk_provider_fetch_payloads','provider_fetch_payloads','p','ae4dce90881dda440e15c9840a665698f9fed0011b58c994ffc4ef63b9d45e2e',true,NULL),
              ('chk_provider_fetch_payloads_source','provider_fetch_payloads','c','1a8d522a3b16a2e9fd38450274f6fe071ddf01d2c6edb8b7c071817c6f95bb75',true,NULL),
              ('chk_provider_fetch_payloads_sha','provider_fetch_payloads','c','6cdfef74f1ab94b20d2db88df8cb88922d7cd7008cb5150b88aa87a7e7acba9e',true,NULL),
              ('chk_provider_fetch_payloads_length','provider_fetch_payloads','c','d77ba96b6b4c91f0831468e8c3cf3b5297f204c7221282a417a95d5425749fd3',true,NULL),
              ('pk_price_observation_attributions','price_observation_attributions','p','a30023c43fe95b7621c55f48c10ec2760357c3d6f5c00055f075d41eaefcd86e',true,NULL),
              ('fk_price_attribution_window','price_observation_attributions','f','299d64815f6570c929dc5a64fe99e4767fba85245c79e9359b8c4459e80986fb',true,'r'),
              ('fk_price_attribution_payload','price_observation_attributions','f','4daaa036b74238e40841fb481de72012e80fbeec6e337407896cdcd8b2b147f2',true,'r'),
              ('chk_price_attribution_sha','price_observation_attributions','c','e823465c0b70501b8ad6632cf6f7295d745a6dc22114f30cd53f04f57a9181f6',true,NULL),
              ('chk_price_attribution_contract','price_observation_attributions','c','a0a9e709a8141cb987cb991ea9109dc01947d7b3e065872ac4d3a2e92d41c8e0',true,NULL),
              ('pk_inflation_observation_attributions','inflation_observation_attributions','p','d437b49347be7b3a384d39a06afc0e019e9cf0205c01c1d65119a3bcb3f2f928',true,NULL),
              ('fk_inflation_attribution_observation','inflation_observation_attributions','f','80cd8c0196e1ab6b7cac97e777f6e309c5dd2cef5d3a50d21c03a414ac90a665',true,'r'),
              ('fk_inflation_attribution_window','inflation_observation_attributions','f','299d64815f6570c929dc5a64fe99e4767fba85245c79e9359b8c4459e80986fb',true,'r'),
              ('fk_inflation_attribution_payload','inflation_observation_attributions','f','4daaa036b74238e40841fb481de72012e80fbeec6e337407896cdcd8b2b147f2',true,'r'),
              ('chk_inflation_attribution_sha','inflation_observation_attributions','c','e823465c0b70501b8ad6632cf6f7295d745a6dc22114f30cd53f04f57a9181f6',true,NULL),
              ('chk_inflation_attribution_contract','inflation_observation_attributions','c','a0a9e709a8141cb987cb991ea9109dc01947d7b3e065872ac4d3a2e92d41c8e0',true,NULL)),
            constraint_drift AS (
              SELECT expected.name FROM expected_constraints expected
              LEFT JOIN pg_constraint contract ON contract.connamespace='public'::regnamespace
               AND contract.conname=expected.name
              LEFT JOIN pg_class relation ON relation.oid=contract.conrelid
              WHERE contract.oid IS NULL OR relation.relname<>expected.relation_name
                 OR contract.contype::text<>expected.kind
                 OR contract.convalidated<>expected.validated
                 OR CASE WHEN contract.contype='f' THEN contract.confdeltype::text END
                      IS DISTINCT FROM expected.delete_action
                 OR encode(sha256(convert_to(pg_get_constraintdef(contract.oid,true),'UTF8')),'hex')<>expected.definition_sha256),
            index_drift AS (
              SELECT 1 FROM pg_index index
              JOIN pg_class relation ON relation.oid=index.indrelid
              JOIN pg_class index_relation ON index_relation.oid=index.indexrelid
              WHERE relation.relnamespace='public'::regnamespace
                AND relation.relname IN ('provider_fetch_payloads','price_observation_attributions',
                                         'inflation_observation_attributions')
                AND (NOT index.indisprimary OR NOT index.indisunique OR NOT index.indisvalid
                     OR NOT index.indisready OR index_relation.relname NOT LIKE 'pk_%')
              UNION ALL
              SELECT 1 WHERE (SELECT count(*) FROM pg_index index
                JOIN pg_class relation ON relation.oid=index.indrelid
               WHERE relation.relnamespace='public'::regnamespace
                 AND relation.relname IN ('provider_fetch_payloads','price_observation_attributions',
                                          'inflation_observation_attributions'))<>3),
            role_contract AS (
              SELECT owner_role,ingestion_capability_role,audit_capability_role
                FROM public.saydin_role_contract
               WHERE singleton=1 AND database_name=current_database()),
            authority_relations(name) AS (VALUES
              ('provider_fetch_payloads'),('price_observation_attributions'),
              ('inflation_observation_attributions')),
            expected_table_acl(name,grantee,grantor,privilege_type,is_grantable) AS (
              SELECT relation.name,role_contract.ingestion_capability_role,role_contract.owner_role,
                     'SELECT',false FROM authority_relations relation CROSS JOIN role_contract
              UNION ALL
              SELECT relation.name,role_contract.audit_capability_role,role_contract.owner_role,
                     'SELECT',false FROM authority_relations relation CROSS JOIN role_contract),
            actual_table_acl AS (
              SELECT relation.relname,grantee.rolname,grantor.rolname,
                     acl.privilege_type,acl.is_grantable
                FROM authority_relations expected
                JOIN pg_class relation ON relation.relnamespace='public'::regnamespace
                 AND relation.relname=expected.name
                CROSS JOIN LATERAL aclexplode(coalesce(
                  relation.relacl,acldefault('r',relation.relowner))) acl
                LEFT JOIN pg_roles grantee ON grantee.oid=acl.grantee
                LEFT JOIN pg_roles grantor ON grantor.oid=acl.grantor
               WHERE acl.grantee<>relation.relowner),
            table_acl_drift AS (
              (SELECT * FROM expected_table_acl EXCEPT ALL SELECT * FROM actual_table_acl)
              UNION ALL (SELECT * FROM actual_table_acl EXCEPT ALL SELECT * FROM expected_table_acl)),
            expected_column_acl(relation_name,column_name,grantee,grantor,privilege_type,is_grantable) AS (
              SELECT relation_name,column_name,role_contract.ingestion_capability_role,
                     role_contract.owner_role,'INSERT',false
                FROM (VALUES
                  ('provider_fetch_payloads','provider_source'),('provider_fetch_payloads','payload_sha256'),
                  ('provider_fetch_payloads','payload_byte_length'),
                  ('price_observation_attributions','asset_id'),('price_observation_attributions','price_date'),
                  ('price_observation_attributions','ingestion_window_id'),('price_observation_attributions','provider_source'),
                  ('price_observation_attributions','payload_sha256'),('price_observation_attributions','source_observation_id'),
                  ('price_observation_attributions','observation_sha256'),('price_observation_attributions','authority_contract_version'),
                  ('inflation_observation_attributions','period_date'),('inflation_observation_attributions','source'),
                  ('inflation_observation_attributions','ingestion_window_id'),('inflation_observation_attributions','provider_source'),
                  ('inflation_observation_attributions','payload_sha256'),('inflation_observation_attributions','source_observation_id'),
                  ('inflation_observation_attributions','observation_sha256'),('inflation_observation_attributions','authority_contract_version')
                ) expected(relation_name,column_name) CROSS JOIN role_contract),
            actual_column_acl AS (
              SELECT relation.relname,attribute.attname,grantee.rolname,grantor.rolname,
                     acl.privilege_type,acl.is_grantable
                FROM pg_attribute attribute
                JOIN pg_class relation ON relation.oid=attribute.attrelid
                CROSS JOIN LATERAL aclexplode(attribute.attacl) acl
                LEFT JOIN pg_roles grantee ON grantee.oid=acl.grantee
                LEFT JOIN pg_roles grantor ON grantor.oid=acl.grantor
               WHERE relation.relnamespace='public'::regnamespace
                 AND relation.relname IN (SELECT name FROM authority_relations)
                 AND attribute.attnum>0 AND NOT attribute.attisdropped
                 AND acl.grantee<>relation.relowner),
            column_acl_drift AS (
              (SELECT * FROM expected_column_acl EXCEPT ALL SELECT * FROM actual_column_acl)
              UNION ALL (SELECT * FROM actual_column_acl EXCEPT ALL SELECT * FROM expected_column_acl)),
            expected_functions(
              name,identity_arguments,result_type,strict,language,volatility,body_sha256) AS (VALUES
              ('saydin_source_raw_allowed','payload jsonb','boolean',true,'sql','i','b656a6a3ccbe9c0e7172fba6738697f98d68de708d263b1ad25fa73237113d07'),
              ('saydin_canonical_observation','payload jsonb','jsonb',true,'sql','i','33535c05ce918127ab5c98fe0bb4bc90082dbe8f2bb881c61a2a45879869a04a'),
              ('enforce_price_point_authority','','trigger',false,'plpgsql','v','7705a66f958768e4e070fc271569084d0e2bc6b87d145b82609138910d5e9ac4'),
              ('enforce_inflation_rate_authority','','trigger',false,'plpgsql','v','2a7f5fc9469f5e13f3f5f776561030b17d60b8f72ff2fd0d2fadf12139764232'),
              ('enforce_observation_attribution','','trigger',false,'plpgsql','v','1097075efe80dd06651f8911d7cc0a7b99ed028de00888495d89997f04e5bb3b'),
              ('enforce_fetch_payload_insert','','trigger',false,'plpgsql','v','60c2b368883fb285ea9769c7af8c31be81417151425839375e78243311375ce4'),
              ('reject_fetch_payload_mutation','','trigger',false,'plpgsql','v','50e1f311966cc9298ad4d41986c552526d4bfd911527d93db85da59acf71eaf4')),
            function_drift AS (
              SELECT expected.name FROM expected_functions expected
              LEFT JOIN pg_proc function ON function.pronamespace='public'::regnamespace
               AND function.proname=expected.name
              LEFT JOIN pg_language language ON language.oid=function.prolang
              CROSS JOIN role_contract
              WHERE function.oid IS NULL
                 OR pg_get_userbyid(function.proowner)<>role_contract.owner_role
                 OR pg_get_function_identity_arguments(function.oid)<>expected.identity_arguments
                 OR pg_get_function_result(function.oid)<>expected.result_type
                 OR function.proisstrict<>expected.strict OR function.prokind<>'f'
                 OR language.lanname<>expected.language
                 OR function.provolatile<>expected.volatility OR function.proparallel<>'u'
                 OR function.prosecdef OR function.proleakproof
                 OR function.proconfig<>ARRAY['search_path=pg_catalog, pg_temp']::text[]
                 OR encode(sha256(convert_to(function.prosrc,'UTF8')),'hex')<>expected.body_sha256),
            expected_function_acl(name,grantee,grantor,privilege_type,is_grantable) AS (
              SELECT expected.name,role_contract.ingestion_capability_role,
                     role_contract.owner_role,'EXECUTE'::text,false
                FROM expected_functions expected CROSS JOIN role_contract
               WHERE expected.name IN ('saydin_source_raw_allowed','saydin_canonical_observation')),
            actual_function_acl AS (
              SELECT expected.name,grantee.rolname,grantor.rolname,
                     acl.privilege_type,acl.is_grantable
                FROM expected_functions expected
                JOIN pg_proc function ON function.pronamespace='public'::regnamespace
                 AND function.proname=expected.name
               CROSS JOIN LATERAL aclexplode(coalesce(
                 function.proacl,acldefault('f',function.proowner))) acl
                LEFT JOIN pg_roles grantee ON grantee.oid=acl.grantee
                LEFT JOIN pg_roles grantor ON grantor.oid=acl.grantor
               WHERE acl.grantee<>function.proowner),
            function_acl_drift AS (
              (SELECT * FROM expected_function_acl EXCEPT ALL SELECT * FROM actual_function_acl)
              UNION ALL
              (SELECT * FROM actual_function_acl EXCEPT ALL SELECT * FROM expected_function_acl)),
            expected_triggers(relation_name,trigger_name,function_schema,function_name,trigger_type,enabled) AS (VALUES
              ('price_points','ts_insert_blocker','_timescaledb_functions','insert_blocker',7,'O'),
              ('price_points','trg_price_points_ingestion_fence','public','enforce_price_point_ingestion_fence',23,'O'),
              ('price_points','trg_price_points_authority','public','enforce_price_point_authority',23,'O'),
              ('inflation_rates','trg_inflation_rates_ingestion_fence','public','enforce_inflation_rate_ingestion_fence',23,'A'),
              ('inflation_rates','trg_inflation_rates_authority','public','enforce_inflation_rate_authority',23,'A'),
              ('provider_fetch_payloads','trg_fetch_payload_append_only','public','reject_fetch_payload_mutation',27,'O'),
              ('provider_fetch_payloads','trg_fetch_payload_live_lease','public','enforce_fetch_payload_insert',7,'O'),
              ('provider_fetch_payloads','trg_fetch_payload_no_truncate','public','reject_fetch_payload_mutation',34,'O'),
              ('price_observation_attributions','trg_price_attribution_append_only','public','enforce_observation_attribution',31,'O'),
              ('price_observation_attributions','trg_price_attribution_no_truncate','public','reject_fetch_payload_mutation',34,'O'),
              ('inflation_observation_attributions','trg_inflation_attribution_append_only','public','enforce_observation_attribution',31,'O'),
              ('inflation_observation_attributions','trg_inflation_attribution_no_truncate','public','reject_fetch_payload_mutation',34,'O')),
            trigger_drift AS (
              (SELECT * FROM expected_triggers EXCEPT ALL
               SELECT relation.relname,trigger.tgname,function_namespace.nspname,function.proname,
                      trigger.tgtype::integer,trigger.tgenabled::text
                 FROM pg_class relation
                 JOIN pg_trigger trigger ON trigger.tgrelid=relation.oid AND NOT trigger.tgisinternal
                 JOIN pg_proc function ON function.oid=trigger.tgfoid
                 JOIN pg_namespace function_namespace ON function_namespace.oid=function.pronamespace
                WHERE relation.relnamespace='public'::regnamespace
                  AND relation.relname IN (SELECT DISTINCT relation_name FROM expected_triggers))
              UNION ALL
              (SELECT relation.relname,trigger.tgname,function_namespace.nspname,function.proname,
                      trigger.tgtype::integer,trigger.tgenabled::text
                 FROM pg_class relation
                 JOIN pg_trigger trigger ON trigger.tgrelid=relation.oid AND NOT trigger.tgisinternal
                 JOIN pg_proc function ON function.oid=trigger.tgfoid
                 JOIN pg_namespace function_namespace ON function_namespace.oid=function.pronamespace
                WHERE relation.relnamespace='public'::regnamespace
                  AND relation.relname IN (SELECT DISTINCT relation_name FROM expected_triggers)
               EXCEPT ALL SELECT * FROM expected_triggers))
            SELECT NOT EXISTS(SELECT 1 FROM constraint_drift)
               AND NOT EXISTS(SELECT 1 FROM index_drift)
               AND NOT EXISTS(SELECT 1 FROM table_acl_drift)
               AND NOT EXISTS(SELECT 1 FROM column_acl_drift)
               AND NOT EXISTS(SELECT 1 FROM function_drift)
               AND NOT EXISTS(SELECT 1 FROM function_acl_drift)
               AND NOT EXISTS(SELECT 1 FROM trigger_drift)
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            _findings.Add("DQ-003", AuditSeverity.Critical,
                "price_authority_structure_drift", "migration-020");
    }

    private async Task AuditApiTrustStructureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var structure = new NpgsqlCommand(
                         ApiTrustAuditSql.Structure, connection, transaction))
        {
            if (await structure.ExecuteScalarAsync(cancellationToken) is not true)
                _findings.Add("DQ-003", AuditSeverity.Critical,
                    "api_trust_structure_drift", "migration-021");
        }

        await using (var access = new NpgsqlCommand("""
                         SELECT has_table_privilege(
                                  current_user,'public.assets','SELECT')
                            AND has_table_privilege(
                                  current_user,'public.asset_catalog_state','SELECT')
                         """, connection, transaction))
        {
            if (await access.ExecuteScalarAsync(cancellationToken) is not true)
            {
                _findings.Add("DQ-003", AuditSeverity.Critical,
                    "asset_catalog_state_drift", "asset-catalog-state");
                return;
            }
        }

        await using (var catalog = new NpgsqlCommand(
                         ApiTrustAuditSql.AssetCatalogState, connection, transaction))
        {
            if (await catalog.ExecuteScalarAsync(cancellationToken) is not true)
                _findings.Add("DQ-003", AuditSeverity.Critical,
                    "asset_catalog_state_drift", "asset-catalog-state");
        }
    }

    private async Task AuditPrincipalRetentionStructureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            PrincipalRetentionAuditSql.Structure, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            _findings.Add("DQ-003", AuditSeverity.Critical,
                "principal_retention_structure_drift", "migration-022");
    }

    private async Task AuditFenceTriggersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var triggers = new Dictionary<string, TriggerFingerprint>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT t.tgname,t.tgenabled,t.tgtype::integer,
                   t.tgrelid='public.price_points'::regclass,
                   t.tgrelid='public.inflation_rates'::regclass,
                   n.nspname,p.proname,p.prorettype='trigger'::regtype,p.pronargs,
                   l.lanname,p.prosecdef,p.provolatile,p.prosrc,
                   coalesce(array_to_string(p.proconfig,E'\n'),'')
            FROM pg_trigger t
            JOIN pg_proc p ON p.oid = t.tgfoid
            JOIN pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_language l ON l.oid=p.prolang
            WHERE t.tgrelid IN ('public.price_points'::regclass, 'public.inflation_rates'::regclass)
              AND t.tgname IN ('trg_price_points_ingestion_fence','trg_inflation_rates_ingestion_fence')
              AND NOT t.tgisinternal
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            triggers.Add(reader.GetString(0), new TriggerFingerprint(
                reader.GetChar(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetBoolean(4),
                reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.GetInt16(8),
                reader.GetString(9), reader.GetBoolean(10), reader.GetChar(11),
                AuditCryptography.Sha256Hex(Encoding.UTF8.GetBytes(reader.GetString(12))),
                AuditCryptography.Sha256Hex(Encoding.UTF8.GetBytes(reader.GetString(13)))));
        if (!triggers.TryGetValue("trg_price_points_ingestion_fence", out var price) ||
            !price.MatchesPrice())
            _findings.Add("DQ-003", AuditSeverity.Critical, "price_fence_trigger_drift", "price_points");
        if (!triggers.TryGetValue("trg_inflation_rates_ingestion_fence", out var inflation) ||
            !inflation.MatchesInflation())
            _findings.Add("DQ-003", AuditSeverity.Critical, "inflation_fence_trigger_drift", "inflation_rates");
    }

    private async Task AuditCalendarAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyDictionary<AuditLane, IReadOnlyList<DatabaseWindow>> laneWindows,
        CancellationToken cancellationToken)
    {
        var releases = new List<Guid>();
        await using (var command = new NpgsqlCommand(
                         "SELECT id FROM public.market_calendar_releases ORDER BY calendar_code COLLATE \"C\", release_version LIMIT $1",
                         connection, transaction))
        {
            command.Parameters.AddWithValue(_budget.MaxCalendarReleases + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) releases.Add(reader.GetGuid(0));
        }
        if (releases.Count > _budget.MaxCalendarReleases)
            throw new AuditRejectedException(
                "calendar_release_budget_exceeded", AuditExitCodes.BudgetRejected);

        long calendarRows = 0;
        foreach (var release in releases)
        {
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM (SELECT 1 FROM public.market_calendar_days WHERE release_id=$1 LIMIT $2) bounded",
                connection, transaction);
            count.Parameters.AddWithValue(release);
            count.Parameters.AddWithValue(_budget.MaxGlobalRows + 1);
            calendarRows += Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (calendarRows > _budget.MaxGlobalRows)
                throw new AuditRejectedException(
                    "calendar_scan_budget_exceeded", AuditExitCodes.BudgetRejected);
        }

        foreach (var release in releases)
        {
            var savepoint = $"calendar_{release:N}";
            await transaction.SaveAsync(savepoint, cancellationToken);
            try
            {
                await using var verify = new NpgsqlCommand(
                    "SELECT public.verify_market_calendar_release_payload($1)", connection, transaction);
                verify.Parameters.AddWithValue(release);
                if (!Convert.ToBoolean(await verify.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
                    _findings.Add("DQ-006", AuditSeverity.Critical, "calendar_payload_invalid", release.ToString("D"));
            }
            catch (PostgresException exception) when (exception.SqlState is "55000" or "23514")
            {
                await transaction.RollbackAsync(savepoint, cancellationToken);
                _findings.Add("DQ-006", AuditSeverity.Critical, "calendar_payload_invalid", release.ToString("D"));
            }
            await transaction.ReleaseAsync(savepoint, cancellationToken);
        }

        var metadata = await QueryViolationsAsync(connection, transaction, AuditSql.CalendarMetadata,
            command =>
            {
                command.Parameters.AddWithValue(releases.ToArray());
                command.Parameters.AddWithValue(input.Manifest.Scope.Lanes
                    .Where(lane => lane.AssetId is not null)
                    .Select(lane => lane.AssetId!.Value).Distinct().ToArray());
            }, cancellationToken);
        AddBatch("DQ-006", AuditSeverity.Critical, metadata);

        foreach (var (lane, windows) in laneWindows.Where(pair =>
                     pair.Key.Source is "tcmb" or "twelvedata"))
        {
            foreach (var window in windows.Where(window => window.State != "pending"))
            {
                if (window.CalendarReleaseId is null)
                {
                    _findings.Add("DQ-006", AuditSeverity.Critical,
                        "calendar_release_missing", window.Id.ToString("D"));
                    continue;
                }
                var coverage = await QueryViolationsAsync(connection, transaction, AuditSql.CalendarWindowCoverage,
                    command => command.Parameters.AddWithValue(window.Id), cancellationToken);
                AddBatch("DQ-006", AuditSeverity.Critical, coverage);
            }
        }
    }

    private async Task<QueryBatch> QueryWindowViolationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DatabaseWindow window,
        AuditLane lane,
        string sql,
        CancellationToken cancellationToken) =>
        await QueryViolationsAsync(connection, transaction, sql,
            command =>
            {
                command.Parameters.AddWithValue(window.Id);
                command.Parameters.AddWithValue(lane.From);
                command.Parameters.AddWithValue(lane.Through);
            }, cancellationToken);

    private async Task<QueryBatch> QueryViolationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        configure(command);
        command.Parameters.AddWithValue(_budget.MaxEvidencePerCheck);
        var samples = new List<RawViolation>();
        long total = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            total = reader.GetInt64(2);
            samples.Add(new RawViolation(reader.GetString(0), reader.GetString(1)));
        }
        return new QueryBatch(total, samples);
    }

    private void AddBatch(string checkId, AuditSeverity severity, QueryBatch batch) =>
        _findings.AddBatch(checkId, severity, batch.TotalCount, batch.Samples);

    private static void AddLaneRange(NpgsqlCommand command, AuditLane lane)
    {
        command.Parameters.AddWithValue(lane.From);
        command.Parameters.AddWithValue(lane.Through);
    }

    private static string LaneKey(AuditLane lane) =>
        $"{lane.Source}|{lane.AssetId?.ToString("D") ?? "global"}|{lane.JobType}|{lane.ContractVersion}";

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private sealed record QueryBatch(long TotalCount, IReadOnlyList<RawViolation> Samples);

    private sealed record TriggerFingerprint(
        char Enabled,
        int Type,
        bool PriceRelation,
        bool InflationRelation,
        string FunctionSchema,
        string FunctionName,
        bool ReturnsTrigger,
        short ArgumentCount,
        string Language,
        bool SecurityDefiner,
        char Volatility,
        string BodySha256,
        string ConfigSha256)
    {
        // Hashes are over pg_proc.prosrc and newline-joined pg_proc.proconfig respectively.
        private const string PriceBodySha256 =
            "4e64afe06288d5700543dd7565505935b7ab74e5a102b00f6d9c56ed4290a416";
        private const string InflationBodySha256 =
            "ceae4a377df47e9a268e0e37f347c8ef17f56afcb819c3d8a762852530fbffaa";
        private const string SearchPathSha256 =
            "20bd6867b3d59c73bbacde2d9a7e7acd1be5b3c154993be84f528ba9d185bd6d";

        public bool MatchesPrice() =>
            Enabled == 'O' && Type == 23 && PriceRelation && !InflationRelation &&
            FunctionSchema == "public" && ReturnsTrigger && ArgumentCount == 0 &&
            Language == "plpgsql" && !SecurityDefiner && Volatility == 'v' &&
            FunctionName == "enforce_price_point_ingestion_fence" &&
            BodySha256 == PriceBodySha256 && ConfigSha256 == SearchPathSha256;

        public bool MatchesInflation() =>
            Enabled == 'A' && Type == 23 && !PriceRelation && InflationRelation &&
            FunctionSchema == "public" && ReturnsTrigger && ArgumentCount == 0 &&
            Language == "plpgsql" && !SecurityDefiner && Volatility == 'v' &&
            FunctionName == "enforce_inflation_rate_ingestion_fence" &&
            BodySha256 == InflationBodySha256 && ConfigSha256 == SearchPathSha256;
    }
}
