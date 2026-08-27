using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DataRepair.IntegrationTests;

public sealed class RepairDatabaseFixture : IAsyncLifetime
{
    private readonly ECDsa planKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa evidenceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa receiptKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private RepairIntegrationEnvironment environment = null!;
    private int day;

    internal string Root { get; private set; } = null!;
    internal string AdminConnectionString => environment.AdminConnectionString;
    internal string AuditLogin => environment.AuditLogin;
    internal string IngestionLogin => environment.IngestionLogin;
    internal string RolePrefix => environment.RolePrefix;
    internal Func<string, string?> RuntimeEnvironment => environment.RuntimeValue;

    public async Task InitializeAsync()
    {
        environment = RepairIntegrationEnvironment.Require();
        Root = Path.Combine(
            Path.GetTempPath(),
            $"saydin-repair-integration-{environment.RunId}-{Guid.NewGuid():N}");
        if (Directory.Exists(Root))
            throw new InvalidOperationException("Repair integration fixture directory already exists.");
        CreatePrivateDirectory(Root);
        await using var connection = new NpgsqlConnection(environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT current_database()=$1
               AND encode(sha256(convert_to(system_identifier::text,'UTF8')),'hex')=$2
               AND (SELECT count(*) FROM public.schema_migrations)=27
               AND (SELECT state FROM public.saydin_migration_control WHERE singleton=1)='ready'
            FROM pg_catalog.pg_control_system()
            """, connection);
        command.Parameters.AddWithValue(environment.Database);
        command.Parameters.AddWithValue(environment.SystemIdentifierSha256);
        if (await command.ExecuteScalarAsync() is not true)
            throw new InvalidOperationException("Repair integration migration trust root is not ready.");
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        planKey.Dispose();
        evidenceKey.Dispose();
        receiptKey.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        await Task.CompletedTask;
    }

    internal async Task<RepairCase> CreateCaseAsync(bool calendarBound = false)
    {
        var id = Guid.CreateVersion7();
        var date = new DateOnly(2038, 1, 1).AddMonths(Interlocked.Increment(ref day));
        await using (var connection = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await connection.OpenAsync();
            var sql = calendarBound
                ? """
                  INSERT INTO public.ingestion_windows(
                      id,source,asset_id,job_type,range_start,range_end,contract_version,state,
                      attempt_count,next_attempt_at,requested_calendar_count,
                      expected_observation_count,raw_item_count,accepted_distinct_count,
                      rejected_count,expected_no_data_count,outcome_code,error_code,
                      created_at,updated_at,completed_at,calendar_release_id)
                  SELECT $1,'tcmb',asset.id,'daily_update',$2,$2,2,'permanent_failed',3,
                         pg_catalog.clock_timestamp(),0,0,0,0,0,0,'retry_exhausted',
                         'unexpected_404',pg_catalog.clock_timestamp(),
                         pg_catalog.clock_timestamp(),pg_catalog.clock_timestamp(),active.release_id
                    FROM public.assets asset
                    JOIN public.asset_market_calendars binding
                      ON binding.asset_id=asset.id AND binding.source='tcmb'
                    JOIN public.market_calendar_active_releases active
                      ON active.calendar_code=binding.calendar_code
                   WHERE asset.source='tcmb'
                   ORDER BY asset.id
                   LIMIT 1
                  """
                : """
                  INSERT INTO public.ingestion_windows(
                      id,source,asset_id,job_type,range_start,range_end,contract_version,state,
                      attempt_count,next_attempt_at,requested_calendar_count,
                      expected_observation_count,raw_item_count,accepted_distinct_count,
                      rejected_count,expected_no_data_count,outcome_code,error_code,
                      created_at,updated_at,completed_at)
                  VALUES($1,'evds',NULL,'inflation_daily',$2,$2,1,'permanent_failed',3,
                      pg_catalog.clock_timestamp(),0,0,0,0,0,0,'retry_exhausted',
                      'provider_contract',pg_catalog.clock_timestamp(),
                      pg_catalog.clock_timestamp(),pg_catalog.clock_timestamp())
                  """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(date);
            if (await command.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("Repair integration fixture calendar binding missing.");
        }

        var snapshot = await LoadSnapshotAsync(id);
        var caseRoot = Path.Combine(Root, id.ToString("N"));
        var input = Path.Combine(caseRoot, "input");
        var evidence = Path.Combine(caseRoot, "evidence");
        var receipts = Path.Combine(caseRoot, "receipts");
        CreatePrivateDirectory(caseRoot);
        CreatePrivateDirectory(input);
        CreatePrivateDirectory(evidence);
        CreatePrivateDirectory(receipts);
        var files = new RepairCaseFiles(
            id, caseRoot, evidence, receipts,
            Path.Combine(input, "plan.json"), Path.Combine(input, "plan.sig"),
            Path.Combine(input, "plan-public.pem"), Path.Combine(input, "evidence-public.pem"),
            Path.Combine(input, "receipt-private.pem"), Path.Combine(input, "approval-token"),
            environment.AuditPasswordFile);
        WritePrivate(files.PlanPublicKeyFile, planKey.ExportSubjectPublicKeyInfoPem());
        WritePrivate(files.EvidencePublicKeyFile, evidenceKey.ExportSubjectPublicKeyInfoPem());
        WritePrivate(files.ReceiptPrivateKeyFile, receiptKey.ExportPkcs8PrivateKeyPem());
        var approval = RandomNumberGenerator.GetBytes(48);
        WritePrivate(files.ApprovalTokenFile, approval);
        var evidenceHash = WriteEvidence(files.EvidenceDirectory);
        var now = DateTimeOffset.UtcNow;
        var plan = new RepairPlan(
            2, RepairCryptography.Sha256Hex(planKey.ExportSubjectPublicKeyInfo()),
            RepairCryptography.Sha256Hex(receiptKey.ExportSubjectPublicKeyInfo()),
            now.AddMinutes(-1), now.AddHours(1), "CHG-REPAIR-INTEGRATION",
            $"nonce-{Guid.NewGuid():N}", RepairCryptography.Sha256Hex(approval),
            new RepairTarget("development", environment.Database,
                environment.SystemIdentifierSha256, environment.DeploymentId, environment.RolePrefix),
            new RepairEvidenceBinding(evidenceHash,
                RepairCryptography.Sha256Hex(evidenceKey.ExportSubjectPublicKeyInfo())),
            new RepairMigrationTrust(EmbeddedRepairMigrationTrust.ManifestSha256,
                EmbeddedRepairMigrationTrust.Entries),
            [
                new RepairOperation("requeue_permanent_window", id,
                    RepairDatabase.SnapshotSha256(snapshot), now.AddMinutes(5), null, null),
                new RepairOperation("manual_review", null, null, null,
                    RepairCryptography.Sha256Hex($"review:{id:N}"), "provider_evidence_required"),
            ]);
        WritePlan(files, plan);
        return new RepairCase(files, plan, snapshot, now);
    }

    internal async Task<(int Exit, string Output, string Error)> RunAsync(
        RepairCase repairCase,
        string mode,
        ICommitBoundary? commitBoundary = null,
        string? auditLogin = null,
        Func<string, string?>? runtime = null,
        IRepairDatabaseFaultInjector? databaseFaultInjector = null,
        int maximumGuardRows = 100_000,
        Action<ReceiptStoreCheckpoint>? receiptCheckpoint = null,
        Func<RepairTrustLease, CancellationToken, Task>? afterLiveTrust = null)
    {
        var args = new List<string>
        {
            mode,
            "--plan", repairCase.Files.PlanFile,
            "--plan-signature", repairCase.Files.PlanSignatureFile,
            "--plan-public-key", repairCase.Files.PlanPublicKeyFile,
            "--evidence-bundle", repairCase.Files.EvidenceDirectory,
            "--evidence-public-key", repairCase.Files.EvidencePublicKeyFile,
            "--audit-login", auditLogin ?? environment.AuditLogin,
            "--audit-password-file", repairCase.Files.AuditPasswordFile,
        };
        if (mode != "dry-run")
            args.AddRange([
                "--approval-token-file", repairCase.Files.ApprovalTokenFile,
                "--receipt-root", repairCase.Files.ReceiptRoot,
                "--receipt-signer-mode", "local-pem",
                "--receipt-private-key", repairCase.Files.ReceiptPrivateKeyFile,
            ]);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await RepairApplication.RunAsync(
            args.ToArray(), output, error, TimeProvider.System,
            runtime ?? environment.RuntimeValue, commitBoundary: commitBoundary,
            databaseFaultInjector: databaseFaultInjector,
            maximumGuardRows: maximumGuardRows,
            receiptCheckpoint: receiptCheckpoint,
            afterLiveTrust: afterLiveTrust);
        return (exit, output.ToString(), error.ToString());
    }

    internal async Task VerifyLiveTrustAfterAsync(
        RepairCase repairCase,
        Func<Task>? afterConnectionsOpen = null,
        RepairTarget? target = null)
    {
        var plan = SignedRepairPlan.LoadAndVerify(
            repairCase.Files.PlanFile, repairCase.Files.PlanSignatureFile,
            repairCase.Files.PlanPublicKeyFile, TimeProvider.System);
        if (target is not null)
            plan = plan with { Plan = plan.Plan with { Target = target } };
        var ingestion = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Ingestion, RuntimeDatabasePooling.Disabled, environment.RuntimeValue);
        await using var ingestionDataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(ingestion);
        await using var lease = await RepairTrustLease.AcquireAsync(
            ingestionDataSource, ingestion.Contract, default);
        var audit = new RuntimeDatabaseOptions(
            LoginPurpose.Audit, ingestion.Contract,
            ingestion.Contract.Login(LoginPurpose.Audit, 1), ingestion.Host, ingestion.Port,
            ingestion.Database, ingestion.SslMode, environment.AuditPasswordFile,
            RuntimeDatabasePooling.Disabled);
        await using var auditDataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(audit);
        if (afterConnectionsOpen is not null) await afterConnectionsOpen();
        await lease.VerifyLiveTrustAsync(auditDataSource, plan, ingestion.Contract, default);
    }

    internal async Task VerifyPreflightAfterAsync(Func<Task> afterConnectionsOpen)
    {
        var ingestion = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Ingestion, RuntimeDatabasePooling.Disabled, environment.RuntimeValue);
        await using var ingestionDataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(ingestion);
        await using var lease = await RepairTrustLease.AcquireAsync(
            ingestionDataSource, ingestion.Contract, default);
        await afterConnectionsOpen();
        var database = new RepairDatabase(ingestionDataSource, lease);
        await database.VerifyPreflightAsync(default);
    }

    internal async Task<int> DirectDryRunAsync(RepairCase repairCase)
    {
        var plan = SignedRepairPlan.LoadAndVerify(
            repairCase.Files.PlanFile, repairCase.Files.PlanSignatureFile,
            repairCase.Files.PlanPublicKeyFile, TimeProvider.System);
        var ingestion = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Ingestion, RuntimeDatabasePooling.Disabled, environment.RuntimeValue);
        await using var ingestionDataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(ingestion);
        await using var lease = await RepairTrustLease.AcquireAsync(
            ingestionDataSource, ingestion.Contract, default);
        var audit = new RuntimeDatabaseOptions(
            LoginPurpose.Audit, ingestion.Contract,
            ingestion.Contract.Login(LoginPurpose.Audit, 1), ingestion.Host, ingestion.Port,
            ingestion.Database, ingestion.SslMode, environment.AuditPasswordFile,
            RuntimeDatabasePooling.Disabled);
        await using var auditDataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(audit);
        await lease.VerifyLiveTrustAsync(auditDataSource, plan, ingestion.Contract, default);
        var database = new RepairDatabase(ingestionDataSource, lease);
        await database.VerifyPreflightAsync(default);
        return await database.DryRunAsync(plan, default);
    }

    internal async Task<WindowSnapshot> LoadSnapshotAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT id,source,asset_id,job_type,range_start,range_end,contract_version,state,
                   lease_owner,lease_token,lease_until,attempt_count,next_attempt_at,
                   requested_calendar_count,expected_observation_count,raw_item_count,
                   accepted_distinct_count,rejected_count,expected_no_data_count,
                   outcome_code,error_code,created_at,updated_at,completed_at,calendar_release_id
              FROM public.ingestion_windows WHERE id=$1
            """, connection);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Repair test window missing.");
        return new WindowSnapshot(
            reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3), reader.GetFieldValue<DateOnly>(4), reader.GetFieldValue<DateOnly>(5),
            reader.GetInt32(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.IsDBNull(10) ? null : Utc(reader.GetDateTime(10)), reader.GetInt32(11),
            Utc(reader.GetDateTime(12)), reader.GetInt32(13), reader.GetInt32(14),
            reader.GetInt32(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20), Utc(reader.GetDateTime(21)),
            Utc(reader.GetDateTime(22)), reader.IsDBNull(23) ? null : Utc(reader.GetDateTime(23)),
            reader.IsDBNull(24) ? null : reader.GetGuid(24));
    }

    internal async Task ExecuteAdminAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        for (var index = 0; index < values.Length; index++)
            command.Parameters.Add(new NpgsqlParameter { Value = values[index] });
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<object?> ExecuteAdminScalarAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        for (var index = 0; index < values.Length; index++)
            command.Parameters.Add(new NpgsqlParameter { Value = values[index] });
        return await command.ExecuteScalarAsync();
    }

    internal async Task<string> LoadMigrationRowJsonAsync(string version)
    {
        var value = await ExecuteAdminScalarAsync(
            "SELECT pg_catalog.row_to_json(m)::text FROM public.schema_migrations m WHERE version=$1",
            version);
        return value as string
            ?? throw new InvalidOperationException($"Migration fixture row {version} is missing.");
    }

    internal Task RestoreMigrationRowJsonAsync(string rowJson) => ExecuteAdminAsync("""
        INSERT INTO public.schema_migrations
        SELECT (pg_catalog.json_populate_record(
            NULL::public.schema_migrations,$1::json)).*;
        """, rowJson);

    internal async Task ExecuteAdminAsReplicaAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var replica = new NpgsqlCommand(
                         "SET LOCAL session_replication_role=replica", connection, transaction))
            await replica.ExecuteNonQueryAsync();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            for (var index = 0; index < values.Length; index++)
                command.Parameters.Add(new NpgsqlParameter { Value = values[index] });
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    internal async Task SeedRunningJobAsync(Guid windowId, string status = "running") =>
        await ExecuteAdminAsync("""
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,started_at,finished_at,status,records_upserted,error_message,
                date_range_start,date_range_end,source,window_id,outcome_code)
            SELECT NULL,'inflation_daily',pg_catalog.clock_timestamp(),
                   CASE WHEN $2='running' THEN NULL ELSE pg_catalog.clock_timestamp() END,
                   $2,NULL,CASE WHEN $2='running' THEN NULL ELSE 'seeded guard row' END,
                   w.range_start,w.range_end,'evds',w.id,
                   CASE WHEN $2='running' THEN NULL ELSE 'seeded_guard' END
              FROM public.ingestion_windows w WHERE w.id=$1
            """, windowId, status);

    internal async Task SeedRelatedGuardStateAsync(RepairCase repairCase)
    {
        await using var connection = new NpgsqlConnection(environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteFixtureCommandAsync(
            connection, transaction, "SET LOCAL session_replication_role=replica");

        var leaseToken = Guid.CreateVersion7();
        await ExecuteFixtureCommandAsync(connection, transaction, """
            UPDATE public.ingestion_windows
               SET state='running',lease_owner='repair-guard-fixture',lease_token=$2,
                   lease_until=pg_catalog.clock_timestamp()+interval '5 minutes',
                   outcome_code=NULL,error_code=NULL,completed_at=NULL
             WHERE id=$1
            """, repairCase.Files.WindowId, leaseToken);
        await ExecuteFixtureCommandAsync(connection, transaction, """
            SELECT pg_catalog.set_config('saydin.ingestion_window_id',$1::text,true),
                   pg_catalog.set_config('saydin.ingestion_lease_token',$2::text,true)
            """, repairCase.Files.WindowId, leaseToken);
        await ExecuteFixtureCommandAsync(connection, transaction, """
            INSERT INTO public.provider_fetch_payloads(
                provider_source,payload_sha256,payload_byte_length)
            VALUES('evds',pg_catalog.sha256(pg_catalog.convert_to($1::text,'UTF8')),
                   pg_catalog.octet_length($1::text))
            """, repairCase.Files.WindowId);
        await ExecuteFixtureCommandAsync(connection, transaction, """
            WITH evidence AS (
              SELECT w.range_start AS period_date,
                     w.range_start::timestamp AT TIME ZONE 'UTC' AS as_of_at,
                     pg_catalog.concat('evds:TP_FG_J0:',
                         pg_catalog.to_char(w.range_start,'YYYY-MM')) AS observation_id
                FROM public.ingestion_windows w WHERE w.id=$1
            ), normalized AS (
              SELECT *,pg_catalog.jsonb_build_object(
                  'as_of_at',as_of_at,'date',pg_catalog.to_char(period_date,'YYYY-MM-DD'),
                  'index_value',999.0000::numeric,'observation_id',observation_id,
                  'provider_source','evds','series','TP.FG.J0') AS source_raw
                FROM evidence
            )
            INSERT INTO public.inflation_rates(
                period_date,index_value,source,provider_source,source_observation_id,
                as_of_at,price_kind,is_final,observation_sha256,
                authority_contract_version,source_raw)
            SELECT period_date,999.0000,'tuik','evds',observation_id,as_of_at,
                   'cpi_index',true,
                   pg_catalog.sha256(pg_catalog.convert_to(
                       public.saydin_canonical_observation(source_raw)::text,'UTF8')),
                   1,source_raw
              FROM normalized
            """, repairCase.Files.WindowId);
        await ExecuteFixtureCommandAsync(connection, transaction, """
            INSERT INTO public.inflation_observation_attributions(
                period_date,source,ingestion_window_id,provider_source,payload_sha256,
                source_observation_id,observation_sha256,authority_contract_version)
            SELECT r.period_date,r.source,w.id,r.provider_source,
                   pg_catalog.sha256(pg_catalog.convert_to($1::text,'UTF8')),
                   r.source_observation_id,r.observation_sha256,r.authority_contract_version
              FROM public.ingestion_windows w
              JOIN public.inflation_rates r ON r.period_date=w.range_start AND r.source='tuik'
             WHERE w.id=$1
            """, repairCase.Files.WindowId);
        await ExecuteFixtureCommandAsync(connection, transaction, """
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,started_at,finished_at,status,records_upserted,error_message,
                date_range_start,date_range_end,source,window_id,outcome_code)
            SELECT NULL,'inflation_daily',
                   pg_catalog.clock_timestamp()+ordinal*interval '1 microsecond',
                   pg_catalog.clock_timestamp()+ordinal*interval '1 microsecond',
                   'failed',NULL,'seeded guard row '||ordinal,
                   w.range_start,w.range_end,'evds',w.id,'seeded_guard'
              FROM public.ingestion_windows w
              CROSS JOIN pg_catalog.generate_series(1,2) AS seeded(ordinal)
             WHERE w.id=$1
            """, repairCase.Files.WindowId);
        await ExecuteFixtureCommandAsync(connection, transaction, """
            UPDATE public.ingestion_windows
               SET state='permanent_failed',lease_owner=NULL,lease_token=NULL,lease_until=NULL,
                   outcome_code=$2,error_code=$3,completed_at=$4
             WHERE id=$1
            """, repairCase.Files.WindowId, repairCase.Preimage.OutcomeCode!,
            repairCase.Preimage.ErrorCode!, repairCase.Preimage.CompletedAt!.Value.UtcDateTime);
        await transaction.CommitAsync();
    }

    private static async Task ExecuteFixtureCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        for (var index = 0; index < values.Length; index++)
            command.Parameters.Add(new NpgsqlParameter { Value = values[index] });
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<Guid> SeedTerminalWindowAsync(RepairCase repairCase, bool sameRangeEnd)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAdminAsync("""
            INSERT INTO public.ingestion_windows(
                id,source,asset_id,job_type,range_start,range_end,contract_version,state,
                attempt_count,next_attempt_at,requested_calendar_count,
                expected_observation_count,raw_item_count,accepted_distinct_count,
                rejected_count,expected_no_data_count,outcome_code,error_code,
                created_at,updated_at,completed_at)
            SELECT $1,source,asset_id,job_type,range_start-1,
                   CASE WHEN $3 THEN range_end ELSE range_end+1 END,
                   contract_version,'permanent_failed',attempt_count,next_attempt_at,
                   requested_calendar_count,expected_observation_count,raw_item_count,
                   accepted_distinct_count,rejected_count,expected_no_data_count,
                   'retry_exhausted','provider_contract',created_at,
                   pg_catalog.clock_timestamp(),pg_catalog.clock_timestamp()
              FROM public.ingestion_windows WHERE id=$2
            """, id, repairCase.Files.WindowId, sameRangeEnd);
        return id;
    }

    internal async Task AdvanceByNormalIngestionAsync(RepairCase repairCase)
    {
        await ExecuteAdminAsync("""
            WITH claimed AS (
              UPDATE public.ingestion_windows
               SET state='running',lease_owner='normal-ingestion-fixture',
                   lease_token=pg_catalog.gen_random_uuid(),
                   lease_until=pg_catalog.clock_timestamp()+interval '5 minutes',
                   attempt_count=attempt_count+1,next_attempt_at=pg_catalog.clock_timestamp(),
                   outcome_code=NULL,error_code=NULL,completed_at=NULL,
                   updated_at=pg_catalog.clock_timestamp()
             WHERE id=$1 AND state='retryable_failed'
             RETURNING id,range_start,range_end
            )
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,started_at,status,date_range_start,date_range_end,source,window_id)
            SELECT NULL,'inflation_daily',pg_catalog.clock_timestamp(),'running',
                   range_start,range_end,'evds',id
              FROM claimed
            """, repairCase.Files.WindowId);
    }

    internal async Task<NpgsqlConnection> HoldTargetLockAsync()
    {
        var contract = RoleContract.Create(environment.DeploymentId, environment.Database,
            environment.SystemIdentifierSha256, environment.RolePrefix);
        var connection = new NpgsqlConnection(environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_lock($1)", connection);
        command.Parameters.AddWithValue(RepairTrustLease.ContractLockKey(contract.TargetLockSha256));
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    internal async Task CleanupAsync(RepairCase repairCase)
    {
        await ExecuteAdminAsReplicaAsync("""
            WITH deleted_attribution AS (
              DELETE FROM public.inflation_observation_attributions
               WHERE ingestion_window_id=$1
              RETURNING period_date,source
            ), deleted_payload AS (
              DELETE FROM public.provider_fetch_payloads
             WHERE provider_source='evds'
               AND payload_sha256=pg_catalog.sha256(pg_catalog.convert_to($1::text,'UTF8'))
              RETURNING payload_sha256
            ), deleted_rate AS (
              DELETE FROM public.inflation_rates r USING public.ingestion_windows w
             WHERE w.id=$1 AND r.period_date=w.range_start AND r.source='tuik'
               AND EXISTS(SELECT 1 FROM deleted_attribution a
                           WHERE a.period_date=r.period_date AND a.source=r.source)
              RETURNING r.period_date
            ), deleted_jobs AS (
              DELETE FROM public.ingestion_jobs WHERE window_id=$1
              RETURNING id
            )
            DELETE FROM public.ingestion_windows WHERE id=$1;
            """, repairCase.Files.WindowId);
    }

    internal void RewritePlan(RepairCase repairCase, RepairPlan plan) =>
        WritePlan(repairCase.Files, plan);

    private void WritePlan(RepairCaseFiles files, RepairPlan plan)
    {
        var bytes = CanonicalJson.Serialize(plan, RepairJsonContext.Default.RepairPlan);
        WritePrivate(files.PlanFile, bytes);
        WritePrivate(files.PlanSignatureFile, planKey.SignData(
            bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    private string WriteEvidence(string directory)
    {
        var content = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes("{\"schemaVersion\":2}"));
        var contentHash = RepairCryptography.Sha256Hex(content);
        WritePrivate(Path.Combine(directory, "evidence-content.json"), content);
        var keyId = RepairCryptography.Sha256Hex(evidenceKey.ExportSubjectPublicKeyInfo());
        var manifest = new DqaEvidenceManifest(
            2, "ECDSA-SHA256-RFC3279-DER", "local-pem", $"local-pem:{keyId}", keyId,
            DateTimeOffset.UtcNow, contentHash,
            [new DqaEvidenceFile("evidence-content.json", content.LongLength, contentHash)]);
        var manifestBytes = CanonicalJson.Serialize(
            manifest, RepairJsonContext.Default.DqaEvidenceManifest);
        WritePrivate(Path.Combine(directory, "manifest.json"), manifestBytes);
        WritePrivate(Path.Combine(directory, "manifest.sha256"),
            Encoding.ASCII.GetBytes(RepairCryptography.Sha256Hex(manifestBytes) + "\n"));
        WritePrivate(Path.Combine(directory, "manifest.sig"), evidenceKey.SignData(
            manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return contentHash;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WritePrivate(string path, string value) =>
        WritePrivate(path, Encoding.UTF8.GetBytes(value));

    private static void WritePrivate(string path, byte[] value)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (File.Exists(path)) File.Delete(path);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(value);
        stream.Flush(flushToDisk: true);
    }

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

internal sealed record RepairCase(
    RepairCaseFiles Files,
    RepairPlan Plan,
    WindowSnapshot Preimage,
    DateTimeOffset Now);

internal sealed record RepairCaseFiles(
    Guid WindowId,
    string Root,
    string EvidenceDirectory,
    string ReceiptRoot,
    string PlanFile,
    string PlanSignatureFile,
    string PlanPublicKeyFile,
    string EvidencePublicKeyFile,
    string ReceiptPrivateKeyFile,
    string ApprovalTokenFile,
    string AuditPasswordFile);
