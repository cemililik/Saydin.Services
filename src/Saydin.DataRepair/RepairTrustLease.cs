using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DataRepair;

internal sealed record VerifiedPhysicalRepairTarget
{
    private VerifiedPhysicalRepairTarget(RepairTarget target) => Target = target;

    public RepairTarget Target { get; }
    public bool IsProduction => Target.Environment == "production";

    internal static VerifiedPhysicalRepairTarget FromLiveTrust(RepairTarget target) => new(target);
}

internal interface IRepairTargetLease
{
    Task VerifyAliveAsync(CancellationToken cancellationToken);
}

internal sealed class RepairTrustLease : IAsyncDisposable, IRepairTargetLease
{
    private readonly NpgsqlConnection lockConnection;
    private readonly long targetLockKey;

    private RepairTrustLease(NpgsqlConnection lockConnection, long targetLockKey)
    {
        this.lockConnection = lockConnection;
        this.targetLockKey = targetLockKey;
    }

    public static async Task<RepairTrustLease> AcquireAsync(
        NpgsqlDataSource ingestionDataSource,
        RoleContract contract,
        CancellationToken cancellationToken)
    {
        var connection = await ingestionDataSource.OpenConnectionAsync(cancellationToken);
        var key = ContractLockKey(contract.TargetLockSha256);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_catalog.pg_try_advisory_lock(@key)", connection);
                command.Parameters.AddWithValue("key", key);
                if (await command.ExecuteScalarAsync(cancellationToken) is true)
                    return new RepairTrustLease(connection, key);
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            throw Rejected("repair_target_lock_timeout");
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<VerifiedPhysicalRepairTarget> VerifyLiveTrustAsync(
        NpgsqlDataSource auditDataSource,
        VerifiedRepairPlan plan,
        RoleContract contract,
        CancellationToken cancellationToken)
    {
        if (lockConnection.State != ConnectionState.Open)
            throw Rejected("repair_target_lock_lost");
        await using var connection = await auditDataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using (var configure = new NpgsqlCommand(
                         "SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='30s';",
                         connection, transaction))
            await configure.ExecuteNonQueryAsync(cancellationToken);

        await VerifyPhysicalTargetAsync(
            connection, transaction, plan.Plan.Target, cancellationToken);
        await VerifyRoleContractAsync(
            connection, transaction, plan.Plan.Target, contract, cancellationToken);
        await VerifyMigrationStateAsync(
            connection, transaction, plan.Plan.MigrationTrust, cancellationToken);
        await VerifyAuditReadOnlyAsync(connection, transaction, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        await VerifyAliveAsync(cancellationToken);
        return VerifiedPhysicalRepairTarget.FromLiveTrust(plan.Plan.Target);
    }

    public async Task VerifyAliveAsync(CancellationToken cancellationToken)
    {
        if (lockConnection.State != ConnectionState.Open)
            throw Rejected("repair_target_lock_lost");
        try
        {
            await using var command = new NpgsqlCommand("SELECT 1", lockConnection);
            if (await command.ExecuteScalarAsync(cancellationToken) is not 1)
                throw Rejected("repair_target_lock_lost");
        }
        catch (RepairRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            throw Rejected("repair_target_lock_lost");
        }
    }

    internal async Task<int> GetBackendProcessIdAsync(CancellationToken cancellationToken)
    {
        await VerifyAliveAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_backend_pid()", lockConnection);
        return await command.ExecuteScalarAsync(cancellationToken) is int processId
            ? processId
            : throw Rejected("repair_target_lock_lost");
    }

    public async ValueTask DisposeAsync()
    {
        if (lockConnection.State == ConnectionState.Open)
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_catalog.pg_advisory_unlock(@key)", lockConnection);
                command.Parameters.AddWithValue("key", targetLockKey);
                await command.ExecuteScalarAsync();
            }
            catch
            {
                // Closing the session is the authoritative lock release fallback.
            }
        }
        await lockConnection.DisposeAsync();
    }

    internal static long ContractLockKey(string targetLockHash) =>
        unchecked((long)Convert.ToUInt64(targetLockHash[..16], 16));

    private static async Task VerifyPhysicalTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RepairTarget target,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_database(),system_identifier::text FROM pg_catalog.pg_control_system()",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != target.Database ||
            !RepairCryptography.FixedEquals(
                Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes(reader.GetString(1)))),
                target.SystemIdentifierSha256) ||
            await reader.ReadAsync(cancellationToken))
            throw Rejected("repair_physical_target_mismatch");
    }

    private static async Task VerifyRoleContractAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RepairTarget target,
        RoleContract contract,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT contract_schema_version,contract_sha256,deployment_id,database_name,
                   system_identifier_sha256,role_prefix,owner_role,migrator_capability_role,
                   api_capability_role,ingestion_capability_role,
                   calendar_importer_capability_role,exporter_capability_role,
                   audit_capability_role,timescale_scheduler_role
              FROM public.saydin_role_contract WHERE singleton=1
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 1 ||
            !RepairCryptography.FixedEquals(
                reader.GetString(1), contract.ContractSha256("2.16.1", "1.1")) ||
            reader.GetString(2) != target.DeploymentId || reader.GetString(3) != target.Database ||
            !RepairCryptography.FixedEquals(reader.GetString(4), target.SystemIdentifierSha256) ||
            reader.GetString(5) != target.RolePrefix || reader.GetString(6) != contract.Owner.Name ||
            reader.GetString(7) != contract.MigratorCapability.Name ||
            reader.GetString(8) != contract.ApiCapability.Name ||
            reader.GetString(9) != contract.IngestionCapability.Name ||
            reader.GetString(10) != contract.CalendarImporterCapability.Name ||
            reader.GetString(11) != contract.ExporterCapability.Name ||
            reader.GetString(12) != contract.AuditCapability.Name ||
            reader.GetString(13) != contract.TimescaleScheduler.Name ||
            await reader.ReadAsync(cancellationToken))
            throw Rejected("repair_role_contract_mismatch");
    }

    private static async Task VerifyMigrationStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RepairMigrationTrust trust,
        CancellationToken cancellationToken)
    {
        await using (var control = new NpgsqlCommand("""
            SELECT state,manifest_checksum FROM public.saydin_migration_control WHERE singleton=1
            """, connection, transaction))
        await using (var reader = await control.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "ready" ||
                !RepairCryptography.FixedEquals(reader.GetString(1), trust.ManifestSha256) ||
                await reader.ReadAsync(cancellationToken))
                throw Rejected("repair_migration_control_not_ready");
        }

        var rows = new Dictionary<string, (string Checksum, string State)>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""
            SELECT version,checksum,state FROM public.schema_migrations ORDER BY version COLLATE "C"
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                if (!rows.TryAdd(reader.GetString(0), (reader.GetString(1), reader.GetString(2))))
                    throw Rejected("repair_migration_set_mismatch");
        }
        if (rows.Count != trust.Migrations.Count) throw Rejected("repair_migration_set_mismatch");
        foreach (var migration in trust.Migrations)
        {
            if (!rows.TryGetValue(migration.Version, out var row) ||
                !RepairCryptography.FixedEquals(row.Checksum, migration.Sha256) ||
                row.State != "succeeded" &&
                    !(migration.Version == "012b_create_exporter_role" &&
                      row.State == "skipped_optional"))
                throw Rejected("repair_migration_checksum_or_state_mismatch");
        }
    }

    private static async Task VerifyAuditReadOnlyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT current_user::text=session_user::text,
                   pg_catalog.has_table_privilege(current_user,'public.schema_migrations','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.saydin_migration_control','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.saydin_role_contract','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_windows','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_windows','INSERT'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_windows','UPDATE'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_windows','DELETE'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_jobs','INSERT'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_jobs','UPDATE')
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !reader.GetBoolean(0) || !reader.GetBoolean(1) || !reader.GetBoolean(2) ||
            !reader.GetBoolean(3) || !reader.GetBoolean(4) || reader.GetBoolean(5) ||
            reader.GetBoolean(6) || reader.GetBoolean(7) || reader.GetBoolean(8) ||
            reader.GetBoolean(9) || await reader.ReadAsync(cancellationToken))
            throw Rejected("repair_audit_role_not_read_only");
    }

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.TargetRejected);
}
