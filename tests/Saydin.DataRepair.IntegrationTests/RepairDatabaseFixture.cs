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
    internal string RolePrefix => environment.RolePrefix;
    internal Func<string, string?> RuntimeEnvironment => environment.RuntimeValue;

    public async Task InitializeAsync()
    {
        environment = RepairIntegrationEnvironment.Require();
        Root = Path.Combine(Path.GetTempPath(), $"saydin-repair-integration-{environment.RunId}");
        if (Directory.Exists(Root))
            throw new InvalidOperationException("Repair integration fixture directory already exists.");
        CreatePrivateDirectory(Root);
        await using var connection = new NpgsqlConnection(environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT current_database()=$1
               AND encode(sha256(convert_to(system_identifier::text,'UTF8')),'hex')=$2
               AND (SELECT count(*) FROM public.schema_migrations)=24
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

    internal async Task<RepairCase> CreateCaseAsync()
    {
        var id = Guid.CreateVersion7();
        var date = new DateOnly(2038, 1, 1).AddDays(Interlocked.Increment(ref day));
        await using (var connection = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
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
                """, connection);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(date);
            await command.ExecuteNonQueryAsync();
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
            1, RepairCryptography.Sha256Hex(planKey.ExportSubjectPublicKeyInfo()),
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
        Func<string, string?>? runtime = null)
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
            runtime ?? environment.RuntimeValue, commitBoundary: commitBoundary);
        return (exit, output.ToString(), error.ToString());
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
        var database = new RepairDatabase(ingestionDataSource);
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
        await ExecuteAdminAsync("""
            WITH deleted_jobs AS (
              DELETE FROM public.ingestion_jobs WHERE window_id=$1 RETURNING id)
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
