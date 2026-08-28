using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Saydin.DataRepair;

internal sealed record DatabaseWindowRecord(WindowSnapshot Snapshot, string DatabaseJson)
{
    public string SnapshotSha256 => RepairDatabase.SnapshotSha256(Snapshot);
    public string ScopeKey =>
        $"{Snapshot.Source}|{Snapshot.AssetId?.ToString("N") ?? "global"}|" +
        $"{Snapshot.JobType}|{Snapshot.ContractVersion}";
}

internal sealed record PreparedRepair(
    long TransactionId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<RepairOperationReceipt> Operations);

internal enum RepairDatabaseCheckpoint
{
    ApplyBeforeCas,
    ApplyAfterCasBeforePostGuard,
    RollbackBeforeCas,
    RollbackAfterCasBeforeVerification,
}

internal interface IRepairDatabaseFaultInjector
{
    Task OnCheckpointAsync(
        RepairDatabaseCheckpoint checkpoint,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid windowId,
        CancellationToken cancellationToken);
}

internal sealed class NoopRepairDatabaseFaultInjector : IRepairDatabaseFaultInjector
{
    public static readonly NoopRepairDatabaseFaultInjector Instance = new();

    public Task OnCheckpointAsync(
        RepairDatabaseCheckpoint checkpoint,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid windowId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RepairDatabase
{
    private const int DefaultMaximumGuardRows = 100_000;
    private readonly NpgsqlDataSource dataSource;
    private readonly IRepairTargetLease targetLease;
    private readonly IRepairDatabaseFaultInjector faultInjector;
    private readonly int maximumGuardRows;

    public RepairDatabase(
        NpgsqlDataSource dataSource,
        IRepairTargetLease targetLease,
        IRepairDatabaseFaultInjector? faultInjector = null,
        int maximumGuardRows = DefaultMaximumGuardRows)
    {
        if (maximumGuardRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumGuardRows));
        this.dataSource = dataSource;
        this.targetLease = targetLease;
        this.faultInjector = faultInjector ?? NoopRepairDatabaseFaultInjector.Instance;
        this.maximumGuardRows = maximumGuardRows;
    }

    public async Task VerifyPreflightAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT current_database(),current_user::text,session_user::text,
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_windows','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_windows','UPDATE'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_windows','DELETE'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_jobs','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.ingestion_jobs','UPDATE'),
                   pg_catalog.has_table_privilege(current_user,'public.price_observation_attributions','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.inflation_observation_attributions','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.price_points','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.inflation_rates','SELECT'),
                   pg_catalog.has_table_privilege(current_user,'public.schema_migrations','SELECT'),
                   pg_catalog.to_regclass('public.ingestion_windows') IS NOT NULL,
                   pg_catalog.to_regclass('public.ingestion_jobs') IS NOT NULL,
                   pg_catalog.to_regclass('public.price_observation_attributions') IS NOT NULL,
                   pg_catalog.to_regclass('public.inflation_observation_attributions') IS NOT NULL,
                   pg_catalog.to_regclass('public.price_points') IS NOT NULL,
                   pg_catalog.to_regclass('public.inflation_rates') IS NOT NULL
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetString(0) is not { Length: > 0 } ||
            reader.GetString(1) != reader.GetString(2) ||
            !reader.GetBoolean(3) || !reader.GetBoolean(4) || reader.GetBoolean(5) ||
            !reader.GetBoolean(6) || !reader.GetBoolean(7) ||
            !reader.GetBoolean(8) || !reader.GetBoolean(9) ||
            !reader.GetBoolean(10) || !reader.GetBoolean(11) || reader.GetBoolean(12) ||
            !reader.GetBoolean(13) || !reader.GetBoolean(14) ||
            !reader.GetBoolean(15) || !reader.GetBoolean(16) ||
            !reader.GetBoolean(17) || !reader.GetBoolean(18) ||
            await reader.ReadAsync(cancellationToken))
            throw TargetRejected("repair_database_acl_rejected");
    }

    public async Task<int> DryRunAsync(
        VerifiedRepairPlan plan,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await ConfigureTransactionAsync(connection, transaction, readOnly: true, cancellationToken);
        await targetLease.VerifyAliveAsync(cancellationToken);
        var ready = 0;
        var guardBudget = new GuardBudget(maximumGuardRows);
        foreach (var indexed in plan.Plan.Operations.Select((operation, index) => (operation, index)))
        {
            if (indexed.operation.Type != "requeue_permanent_window") continue;
            var row = await ReadWindowAsync(
                connection, transaction, indexed.operation.WindowId!.Value, false, cancellationToken);
            await ValidateApplyPreconditionsAsync(
                connection, transaction, indexed.operation, row, cancellationToken);
            _ = await ComputeGuardAsync(
                connection, transaction, row.Snapshot.Id, guardBudget, cancellationToken,
                lockRows: false);
            ready++;
        }
        await transaction.RollbackAsync(cancellationToken);
        return ready;
    }

    public async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction, PreparedRepair Prepared)>
        PrepareApplyAsync(
            VerifiedRepairPlan plan,
            CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, readOnly: false, cancellationToken);
            await LockPlanAsync(connection, transaction, plan.PlanSha256, cancellationToken);
            await targetLease.VerifyAliveAsync(cancellationToken);

            var mutable = plan.Plan.Operations.Select((operation, index) => (operation, index))
                .Where(item => item.operation.Type == "requeue_permanent_window").ToArray();
            var preliminary = new List<(RepairOperation Operation, int Index, DatabaseWindowRecord Row)>();
            foreach (var item in mutable)
                preliminary.Add((item.operation, item.index, await ReadWindowAsync(
                    connection, transaction, item.operation.WindowId!.Value, false, cancellationToken)));

            var results = new RepairOperationReceipt[plan.Plan.Operations.Count];
            var guardBudget = new GuardBudget(maximumGuardRows);
            foreach (var item in preliminary.OrderBy(item => item.Row.ScopeKey, StringComparer.Ordinal))
            {
                await LockScopeAsync(connection, transaction, item.Row.ScopeKey, cancellationToken);
                var current = await ReadWindowAsync(
                    connection, transaction, item.Operation.WindowId!.Value, true, cancellationToken);
                await ValidateApplyPreconditionsAsync(
                    connection, transaction, item.Operation, current, cancellationToken);
                var guard = await ComputeGuardAsync(
                    connection, transaction, current.Snapshot.Id, guardBudget, cancellationToken);
                await targetLease.VerifyAliveAsync(cancellationToken);
                await faultInjector.OnCheckpointAsync(
                    RepairDatabaseCheckpoint.ApplyBeforeCas, connection, transaction,
                    current.Snapshot.Id, cancellationToken);
                var updated = await RequeueCasAsync(
                    connection, transaction, current, item.Operation.NextAttemptAtUtc!.Value,
                    plan.Plan.SchemaVersion >= 2 && current.Snapshot.CalendarReleaseId is not null,
                    cancellationToken);
                await faultInjector.OnCheckpointAsync(
                    RepairDatabaseCheckpoint.ApplyAfterCasBeforePostGuard, connection, transaction,
                    current.Snapshot.Id, cancellationToken);
                var postGuard = await ComputeGuardAsync(
                    connection, transaction, current.Snapshot.Id, guardBudget, cancellationToken);
                if (!RepairCryptography.FixedEquals(guard, postGuard))
                    throw Rejected("repair_guard_changed_inside_transaction");
                await targetLease.VerifyAliveAsync(cancellationToken);
                results[item.Index] = new RepairOperationReceipt(
                    item.Index,
                    "requeued",
                    current.SnapshotSha256,
                    updated.SnapshotSha256,
                    postGuard,
                    Rollback(current.Snapshot));
            }
            foreach (var item in plan.Plan.Operations.Select((operation, index) => (operation, index))
                         .Where(item => item.operation.Type != "requeue_permanent_window"))
                results[item.index] = new RepairOperationReceipt(
                    item.index,
                    item.operation.Type == "refetch"
                        ? "work_order_refetch" : "work_order_manual_review",
                    null, null, null, null);

            var (transactionId, createdAt) = await ReadTransactionIdentityAsync(
                connection, transaction, cancellationToken);
            return (connection, transaction, new PreparedRepair(transactionId, createdAt, results));
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction, PreparedRepair Prepared)>
        PrepareRollbackAsync(
            VerifiedRepairPlan plan,
            VerifiedRepairReceipt applyReceipt,
            CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, readOnly: false, cancellationToken);
            await LockPlanAsync(connection, transaction, plan.PlanSha256, cancellationToken);
            await targetLease.VerifyAliveAsync(cancellationToken);
            var receiptByIndex = applyReceipt.Receipt.Operations.ToDictionary(item => item.Index);
            var mutable = plan.Plan.Operations.Select((operation, index) => (operation, index))
                .Where(item => item.operation.Type == "requeue_permanent_window").ToArray();
            var preliminary = new List<(RepairOperation Operation, int Index, DatabaseWindowRecord Row)>();
            foreach (var item in mutable)
                preliminary.Add((item.operation, item.index, await ReadWindowAsync(
                    connection, transaction, item.operation.WindowId!.Value, false, cancellationToken)));

            var results = new RepairOperationReceipt[plan.Plan.Operations.Count];
            var guardBudget = new GuardBudget(maximumGuardRows);
            foreach (var item in preliminary.OrderBy(item => item.Row.ScopeKey, StringComparer.Ordinal))
            {
                var prior = receiptByIndex[item.Index];
                var calendarRebind = applyReceipt.Receipt.SchemaVersion >= 2 &&
                                     prior.RollbackState?.CalendarReleaseId is not null;
                if (prior.Result != "requeued" || prior.RollbackState is null ||
                    prior.PostimageSha256 is null || prior.PreimageSha256 is null ||
                    prior.GuardSha256 is null)
                    throw Rejected("rollback_receipt_operation_invalid");
                await LockScopeAsync(connection, transaction, item.Row.ScopeKey, cancellationToken);
                var current = await ReadWindowAsync(
                    connection, transaction, item.Operation.WindowId!.Value, true, cancellationToken);
                if (!RepairCryptography.FixedEquals(current.SnapshotSha256, prior.PostimageSha256) ||
                    current.Snapshot.State != (calendarRebind ? "pending" : "retryable_failed") ||
                    current.Snapshot.OutcomeCode != (calendarRebind ? null : "operator_requeue") ||
                    current.Snapshot.ErrorCode != (calendarRebind ? null : "operator_requeue") ||
                    current.Snapshot.LeaseOwner is not null || current.Snapshot.LeaseToken is not null ||
                    current.Snapshot.LeaseUntil is not null ||
                    await HasRunningJobAsync(
                        connection, transaction, current.Snapshot.Id, cancellationToken))
                    throw Rejected("rollback_postimage_changed");
                var guard = await ComputeGuardAsync(
                    connection, transaction, current.Snapshot.Id, guardBudget, cancellationToken);
                if (!RepairCryptography.FixedEquals(guard, prior.GuardSha256))
                    throw Rejected("rollback_related_state_changed");
                await targetLease.VerifyAliveAsync(cancellationToken);
                await faultInjector.OnCheckpointAsync(
                    RepairDatabaseCheckpoint.RollbackBeforeCas, connection, transaction,
                    current.Snapshot.Id, cancellationToken);
                await RollbackCasAsync(
                    connection, transaction, current, prior.RollbackState,
                    calendarRebind, cancellationToken);
                await faultInjector.OnCheckpointAsync(
                    RepairDatabaseCheckpoint.RollbackAfterCasBeforeVerification,
                    connection, transaction, current.Snapshot.Id, cancellationToken);
                var restored = await ReadWindowAsync(
                    connection, transaction, current.Snapshot.Id, true, cancellationToken);
                if (!RepairCryptography.FixedEquals(restored.SnapshotSha256, prior.PreimageSha256) ||
                    !RepairCryptography.FixedEquals(
                        restored.SnapshotSha256, item.Operation.PreimageSha256!))
                    throw Rejected("rollback_preimage_restore_failed");
                var restoredGuard = await ComputeGuardAsync(
                    connection, transaction, current.Snapshot.Id, guardBudget, cancellationToken);
                if (!RepairCryptography.FixedEquals(restoredGuard, prior.GuardSha256))
                    throw Rejected("rollback_related_state_changed");
                await targetLease.VerifyAliveAsync(cancellationToken);
                results[item.Index] = new RepairOperationReceipt(
                    item.Index, "rolled_back", current.SnapshotSha256,
                    restored.SnapshotSha256, restoredGuard, null);
            }
            foreach (var item in plan.Plan.Operations.Select((operation, index) => (operation, index))
                         .Where(item => item.operation.Type != "requeue_permanent_window"))
            {
                var prior = receiptByIndex[item.index];
                if (prior.Result is not ("work_order_refetch" or "work_order_manual_review"))
                    throw Rejected("rollback_receipt_operation_invalid");
                results[item.index] = prior;
            }

            var (transactionId, createdAt) = await ReadTransactionIdentityAsync(
                connection, transaction, cancellationToken);
            return (connection, transaction, new PreparedRepair(transactionId, createdAt, results));
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> MatchesReceiptStateAsync(
        VerifiedRepairPlan plan,
        VerifiedRepairReceipt receipt,
        bool postState,
        CancellationToken cancellationToken,
        bool allowNormalIngestionProgress = false,
        bool verifyGuard = true)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await ConfigureTransactionAsync(connection, transaction, readOnly: true, cancellationToken);
        var byIndex = receipt.Receipt.Operations.ToDictionary(item => item.Index);
        await targetLease.VerifyAliveAsync(cancellationToken);
        var guardBudget = new GuardBudget(maximumGuardRows);
        foreach (var item in plan.Plan.Operations.Select((operation, index) => (operation, index)))
        {
            if (item.operation.Type != "requeue_permanent_window") continue;
            var expected = byIndex[item.index];
            var hash = postState ? expected.PostimageSha256 : expected.PreimageSha256;
            if (hash is null) return false;
            var current = await ReadWindowAsync(
                connection, transaction, item.operation.WindowId!.Value, false, cancellationToken);
            if (!RepairCryptography.FixedEquals(current.SnapshotSha256, hash))
            {
                if (!postState || !allowNormalIngestionProgress || receipt.Receipt.Mode != "apply" ||
                    expected.Result != "requeued" || !await HasNormalIngestionProgressAsync(
                        connection, transaction, current.Snapshot,
                        receipt.Receipt.CreatedAtUtc, cancellationToken))
                    return false;
                continue;
            }
            if (postState && verifyGuard && expected.GuardSha256 is { } expectedGuard)
            {
                var currentGuard = await ComputeGuardAsync(
                    connection, transaction, current.Snapshot.Id, guardBudget, cancellationToken,
                    lockRows: false);
                if (!RepairCryptography.FixedEquals(currentGuard, expectedGuard)) return false;
            }
        }
        await transaction.RollbackAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> HasNormalIngestionProgressAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WindowSnapshot window,
        DateTimeOffset receiptCreatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (window.UpdatedAt < receiptCreatedAtUtc || window.State is not (
                "running" or "succeeded" or "expected_no_data" or "retryable_failed" or
                "permanent_failed" or "cancelled" or "abandoned"))
            return false;
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM public.ingestion_jobs
                 WHERE window_id=@window AND started_at>=@receipt_created)
            """, connection, transaction);
        command.Parameters.AddWithValue("window", window.Id);
        command.Parameters.AddWithValue("receipt_created", receiptCreatedAtUtc.UtcDateTime);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public static string SnapshotSha256(WindowSnapshot snapshot) =>
        RepairCryptography.Sha256Hex(CanonicalJson.Serialize(
            snapshot, RepairJsonContext.Default.WindowSnapshot));

    private static async Task ValidateApplyPreconditionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RepairOperation operation,
        DatabaseWindowRecord row,
        CancellationToken cancellationToken)
    {
        if (!RepairCryptography.FixedEquals(row.SnapshotSha256, operation.PreimageSha256!) ||
            row.Snapshot.State != "permanent_failed" || row.Snapshot.CompletedAt is null ||
            row.Snapshot.ErrorCode is null || row.Snapshot.OutcomeCode is null ||
            row.Snapshot.LeaseOwner is not null || row.Snapshot.LeaseToken is not null ||
            row.Snapshot.LeaseUntil is not null)
            throw Rejected("repair_preimage_rejected");
        if (await HasRunningJobAsync(connection, transaction, row.Snapshot.Id, cancellationToken))
            throw Rejected("repair_running_job_rejected");
        if (await HasNewerTerminalWindowAsync(
                connection, transaction, row.Snapshot, cancellationToken))
            throw Rejected("repair_newer_terminal_window_rejected");
    }

    private static async Task<DatabaseWindowRecord> ReadWindowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid windowId,
        bool lockRow,
        CancellationToken cancellationToken)
    {
        var sql = WindowSelect + (lockRow ? " FOR UPDATE" : string.Empty);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", windowId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw Rejected("repair_window_missing");
        var result = ReadWindow(reader);
        if (await reader.ReadAsync(cancellationToken)) throw Rejected("repair_window_duplicate");
        return result;
    }

    private static DatabaseWindowRecord ReadWindow(NpgsqlDataReader reader) =>
        new(new WindowSnapshot(
                reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4), reader.GetFieldValue<DateOnly>(5),
                reader.GetInt32(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : Utc(reader.GetDateTime(10)),
                reader.GetInt32(11), Utc(reader.GetDateTime(12)),
                reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15),
                reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                Utc(reader.GetDateTime(21)), Utc(reader.GetDateTime(22)),
                reader.IsDBNull(23) ? null : Utc(reader.GetDateTime(23)),
                reader.IsDBNull(24) ? null : reader.GetGuid(24)),
            reader.GetString(25));

    private static async Task<DatabaseWindowRecord> RequeueCasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DatabaseWindowRecord current,
        DateTimeOffset nextAttemptAtUtc,
        bool calendarRebind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(UpdateRequeue, connection, transaction);
        command.Parameters.AddWithValue("id", current.Snapshot.Id);
        command.Parameters.AddWithValue("preimage", NpgsqlDbType.Jsonb, current.DatabaseJson);
        command.Parameters.AddWithValue("next_attempt", nextAttemptAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("calendar_rebind", calendarRebind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw Rejected("repair_cas_failed");
        var result = ReadWindow(reader);
        if (await reader.ReadAsync(cancellationToken)) throw Rejected("repair_cas_failed");
        return result;
    }

    private static async Task<DatabaseWindowRecord> RollbackCasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DatabaseWindowRecord current,
        RollbackState state,
        bool restoreCalendarRelease,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(UpdateRollback, connection, transaction);
        command.Parameters.AddWithValue("id", current.Snapshot.Id);
        command.Parameters.AddWithValue("postimage", NpgsqlDbType.Jsonb, current.DatabaseJson);
        command.Parameters.AddWithValue("state", state.State);
        command.Parameters.AddWithValue("next_attempt", state.NextAttemptAt.UtcDateTime);
        AddNullableText(command, "outcome", state.OutcomeCode);
        AddNullableText(command, "error", state.ErrorCode);
        command.Parameters.AddWithValue("updated", state.UpdatedAt.UtcDateTime);
        command.Parameters.AddWithValue("restore_calendar", restoreCalendarRelease);
        command.Parameters.Add(new NpgsqlParameter("calendar_release", NpgsqlDbType.Uuid)
        {
            Value = state.CalendarReleaseId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("completed", NpgsqlDbType.TimestampTz)
        {
            Value = state.CompletedAt?.UtcDateTime ?? (object)DBNull.Value,
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw Rejected("rollback_cas_failed");
        var result = ReadWindow(reader);
        if (await reader.ReadAsync(cancellationToken)) throw Rejected("rollback_cas_failed");
        return result;
    }

    private static async Task<string> ComputeGuardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid windowId,
        GuardBudget budget,
        CancellationToken cancellationToken,
        bool lockRows = true)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var queries = new[]
        {
            ("jobs", "SELECT pg_catalog.to_jsonb(j)::text FROM public.ingestion_jobs j WHERE j.window_id=@window ORDER BY j.id" + (lockRows ? " FOR UPDATE OF j" : "")),
            // Normal ingestion writers hold the same scope advisory lock acquired by
            // PrepareApply/PrepareRollback. The managed role intentionally has SELECT
            // (not UPDATE) on append-only attribution/data tables, so row-lock clauses
            // would be both unauthorized and redundant inside that scope fence.
            ("price-attribution", "SELECT pg_catalog.to_jsonb(a)::text FROM public.price_observation_attributions a WHERE a.ingestion_window_id=@window ORDER BY a.asset_id,a.price_date,a.payload_sha256"),
            ("price-data", "SELECT pg_catalog.to_jsonb(p)::text FROM public.price_points p JOIN (SELECT DISTINCT asset_id,price_date FROM public.price_observation_attributions WHERE ingestion_window_id=@window) a USING(asset_id,price_date) ORDER BY p.asset_id,p.price_date"),
            ("inflation-attribution", "SELECT pg_catalog.to_jsonb(a)::text FROM public.inflation_observation_attributions a WHERE a.ingestion_window_id=@window ORDER BY a.period_date,a.source,a.payload_sha256"),
            ("inflation-data", "SELECT pg_catalog.to_jsonb(r)::text FROM public.inflation_rates r JOIN (SELECT DISTINCT period_date,source FROM public.inflation_observation_attributions WHERE ingestion_window_id=@window) a USING(period_date,source) ORDER BY r.period_date,r.source"),
        };
        foreach (var (label, sql) in queries)
        {
            Append(hash, Encoding.ASCII.GetBytes(label));
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("window", windowId);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Take();
                Append(hash, Encoding.UTF8.GetBytes(reader.GetString(0)));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static async Task<bool> HasRunningJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM public.ingestion_jobs
                           WHERE window_id=@id AND status='running')
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<bool> HasNewerTerminalWindowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WindowSnapshot window,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM public.ingestion_windows newer
                 WHERE newer.id<>@id AND newer.source=@source
                   AND newer.asset_id IS NOT DISTINCT FROM @asset
                   AND newer.job_type=@job AND newer.contract_version=@contract
                   AND (newer.range_end>@range_end OR
                        (newer.range_end=@range_end AND newer.range_start<@range_start))
                   AND newer.state IN ('succeeded','expected_no_data','permanent_failed'))
            """, connection, transaction);
        command.Parameters.AddWithValue("id", window.Id);
        command.Parameters.AddWithValue("source", window.Source);
        command.Parameters.Add(new NpgsqlParameter("asset", NpgsqlDbType.Uuid)
        {
            Value = window.AssetId ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("job", window.JobType);
        command.Parameters.AddWithValue("contract", window.ContractVersion);
        command.Parameters.AddWithValue("range_end", window.RangeEnd);
        command.Parameters.AddWithValue("range_start", window.RangeStart);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task LockPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string planSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@plan,1))",
            connection, transaction);
        command.Parameters.AddWithValue("plan", planSha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LockScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@scope,0))",
            connection, transaction);
        command.Parameters.AddWithValue("scope", scopeKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ConfigureTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SET TRANSACTION {(readOnly ? "READ ONLY" : "READ WRITE")}; " +
            "SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'; " +
            "SET LOCAL idle_in_transaction_session_timeout='45s';",
            connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(long TransactionId, DateTimeOffset CreatedAt)> ReadTransactionIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.txid_current()::bigint,pg_catalog.clock_timestamp()",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw Rejected("transaction_identity_missing");
        var result = (reader.GetInt64(0), Utc(reader.GetDateTime(1)));
        if (await reader.ReadAsync(cancellationToken)) throw Rejected("transaction_identity_invalid");
        return result;
    }

    private static RollbackState Rollback(WindowSnapshot snapshot) =>
        new(snapshot.State, snapshot.NextAttemptAt, snapshot.OutcomeCode,
            snapshot.ErrorCode, snapshot.UpdatedAt, snapshot.CompletedAt,
            snapshot.CalendarReleaseId);

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value ?? (object)DBNull.Value,
        });

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class GuardBudget(int remaining)
    {
        private int remaining = remaining;

        public void Take()
        {
            if (remaining-- <= 0) throw Rejected("repair_guard_row_budget_exceeded");
        }
    }

    private const string WindowColumns = """
        id,source,asset_id,job_type,range_start,range_end,contract_version,state,
        lease_owner,lease_token,lease_until,attempt_count,next_attempt_at,
        requested_calendar_count,expected_observation_count,raw_item_count,
        accepted_distinct_count,rejected_count,expected_no_data_count,
        outcome_code,error_code,created_at,updated_at,completed_at,calendar_release_id,
        pg_catalog.to_jsonb(ingestion_windows)::text
        """;

    private const string WindowSelect = "SELECT " + WindowColumns +
        " FROM public.ingestion_windows WHERE id=@id";

    private const string UpdateRequeue = """
        UPDATE public.ingestion_windows
           SET state=CASE WHEN @calendar_rebind THEN 'pending' ELSE 'retryable_failed' END,
               lease_owner=NULL,lease_token=NULL,lease_until=NULL,
               next_attempt_at=@next_attempt,
               outcome_code=CASE WHEN @calendar_rebind THEN NULL ELSE 'operator_requeue' END,
               error_code=CASE WHEN @calendar_rebind THEN NULL ELSE 'operator_requeue' END,
               completed_at=NULL,
               calendar_release_id=CASE WHEN @calendar_rebind THEN NULL ELSE calendar_release_id END,
               updated_at=pg_catalog.clock_timestamp()
         WHERE id=@id AND state='permanent_failed'
           AND pg_catalog.to_jsonb(ingestion_windows)=@preimage::jsonb
        RETURNING
        """ + " " + WindowColumns;

    private const string UpdateRollback = """
        UPDATE public.ingestion_windows
           SET state=@state,next_attempt_at=@next_attempt,outcome_code=@outcome,error_code=@error,
               updated_at=@updated,completed_at=@completed,
               calendar_release_id=CASE WHEN @restore_calendar THEN @calendar_release
                                        ELSE calendar_release_id END,
               lease_owner=NULL,lease_token=NULL,lease_until=NULL
         WHERE id=@id AND state IN ('pending','retryable_failed')
           AND pg_catalog.to_jsonb(ingestion_windows)=@postimage::jsonb
        RETURNING
        """ + " " + WindowColumns;

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.Rejected);

    private static RepairRejectedException TargetRejected(string code) =>
        new(code, RepairExitCodes.TargetRejected);
}
