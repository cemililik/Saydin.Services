using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Saydin.DatabaseMigrator;

internal sealed record MigrationImpactPreflightSnapshot(
    long RelationBytes,
    long CompressedBytes,
    long TablespaceUsedBytes,
    long FreeBytesAfter,
    int HeadroomRatioBasisPoints,
    int WaitingLocks,
    double OldestBlockingTransactionSeconds,
    int StreamingReplicas,
    long MaximumReplicaLagBytes,
    long MaximumSlotRetentionBytes);

internal static class MigrationImpactPreflight
{
    // Operations above this bound require a purpose-built online plan. A signed
    // manifest can lower budgets, but cannot raise this compiled safety ceiling.
    internal const long MaximumTransactionalHeavyRelationBytes = 64L * 1024 * 1024;

    public static async Task<MigrationImpactPreflightSnapshot> VerifyAsync(
        NpgsqlConnection connection,
        MigratorOptions options,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        CancellationToken cancellationToken)
    {
        var previousTimeouts = await ReadSessionTimeoutsAsync(connection, cancellationToken);
        try
        {
            await SetSessionTimeoutsAsync(connection, impact, cancellationToken);
            return await VerifyCoreAsync(
                connection, options, migration, impact, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState is
            PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled)
        {
            throw new MigratorRejectedException(
                "migration_impact_lock_budget_exceeded", migration.Version, exception);
        }
        finally
        {
            await TryRestoreSessionTimeoutsAsync(
                connection, previousTimeouts.Lock, previousTimeouts.Statement, cancellationToken);
        }
    }

    private static async Task<(string Lock, string Statement)> ReadSessionTimeoutsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('lock_timeout'),current_setting('statement_timeout')", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_impact_timeout_state_unavailable");
        var value = (reader.GetString(0), reader.GetString(1));
        if (await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_impact_timeout_state_ambiguous");
        return value;
    }

    private static async Task<MigrationImpactPreflightSnapshot> VerifyCoreAsync(
        NpgsqlConnection connection,
        MigratorOptions options,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        CancellationToken cancellationToken)
    {
        var document = impact.Document;
        if (!string.Equals(document.Target.Database, options.Database, StringComparison.Ordinal) ||
            !CryptographicEquals(
                document.Target.SystemIdentifierSha256,
                options.Contract.SystemIdentifierSha256))
            throw new MigratorRejectedException("migration_impact_target_mismatch", migration.Version);
        if (document.Budgets.LockTimeoutMilliseconds > options.Timeouts.Lock.TotalMilliseconds ||
            document.Budgets.StatementTimeoutMilliseconds > options.Timeouts.Command.TotalMilliseconds ||
            document.Budgets.TotalTimeoutSeconds > options.Timeouts.Total.TotalSeconds)
            throw new MigratorRejectedException("migration_impact_budget_exceeds_runner", migration.Version);

        await VerifyTerminalPredecessorAsync(
            connection, document.Target, cancellationToken);
        var relationMetrics = new List<RelationMetrics>();
        foreach (var relation in document.Relations)
            relationMetrics.Add(await ReadRelationMetricsAsync(
                connection, relation, cancellationToken));
        var relationBytes = relationMetrics.Sum(metric => checked(metric.RootBytes + metric.ChunkBytes));
        var compressedBytes = relationMetrics.Sum(metric => metric.CompressedBytes);
        var totalRelationBytes = checked(relationBytes + compressedBytes);
        if (totalRelationBytes > document.Budgets.MaxRelationBytes ||
            compressedBytes > document.Budgets.MaxCompressedBytes)
            throw new MigratorRejectedException("migration_impact_relation_budget_exceeded", migration.Version);
        if (impact.Mode == MigrationExecutionMode.Transactional &&
            impact.SqlAnalysis.Classifications.Any(SqlImpactKinds.Heavy.Contains) &&
            totalRelationBytes > MaximumTransactionalHeavyRelationBytes)
            throw new MigratorRejectedException("migration_online_mode_required", migration.Version);
        if (impact.Mode == MigrationExecutionMode.Transactional &&
            impact.SqlAnalysis.Classifications.Contains(SqlImpactKinds.LargeDml, StringComparer.Ordinal))
            throw new MigratorRejectedException("migration_online_mode_required", migration.Version);

        var tablespaces = relationMetrics.Select(metric => metric.Tablespace)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (tablespaces.Length != 1)
            throw new MigratorRejectedException("migration_impact_tablespace_contract_mismatch", migration.Version);
        var tablespaceUsed = await ScalarAsync<long>(connection,
            "SELECT pg_catalog.pg_tablespace_size($1)::bigint", cancellationToken, tablespaces[0]);
        var freeAfter = checked(document.Budgets.DeclaredTablespaceCapacityBytes -
                                tablespaceUsed - document.Budgets.EstimatedAdditionalBytes);
        var ratio = freeAfter <= 0
            ? 0
            : (int)Math.Min(10_000,
                (decimal)freeAfter * 10_000 / document.Budgets.DeclaredTablespaceCapacityBytes);
        if (freeAfter < document.Budgets.MinFreeBytesAfter ||
            ratio < document.Budgets.MinHeadroomRatioBasisPoints)
            throw new MigratorRejectedException("migration_impact_disk_headroom_insufficient", migration.Version);

        var relationOids = relationMetrics.SelectMany(metric => metric.RelationOids)
            .Distinct().ToArray();
        var lockMetrics = await ReadLockMetricsAsync(connection, relationOids, cancellationToken);
        if (lockMetrics.WaitingLocks > document.Budgets.MaxWaitingLocks ||
            lockMetrics.OldestTransactionSeconds > document.Budgets.MaxBlockingTransactionAgeSeconds)
            throw new MigratorRejectedException("migration_impact_lock_budget_exceeded", migration.Version);

        var replication = await ReadReplicationMetricsAsync(connection, cancellationToken);
        if (!replication.Visible ||
            replication.StreamingReplicas < document.Budgets.MinimumStreamingReplicas ||
            replication.MaximumLagBytes > document.Budgets.MaxReplicaLagBytes)
            throw new MigratorRejectedException("migration_impact_replica_budget_exceeded", migration.Version);
        var slots = await ReadSlotMetricsAsync(connection, cancellationToken);
        if (!slots.Visible || slots.MaximumRetentionBytes > document.Budgets.MaxSlotRetentionBytes ||
            document.Budgets.RequireAllSlotsActive && slots.InactiveSlots > 0)
            throw new MigratorRejectedException("migration_impact_slot_budget_exceeded", migration.Version);
        if (document.Budgets.EstimatedAdditionalBytes > document.Budgets.MaxProjectedWalBytes)
            throw new MigratorRejectedException("migration_impact_wal_budget_exceeded", migration.Version);

        return new MigrationImpactPreflightSnapshot(
            relationBytes,
            compressedBytes,
            tablespaceUsed,
            freeAfter,
            ratio,
            lockMetrics.WaitingLocks,
            lockMetrics.OldestTransactionSeconds,
            replication.StreamingReplicas,
            replication.MaximumLagBytes,
            slots.MaximumRetentionBytes);
    }

    private static async Task SetSessionTimeoutsAsync(
        NpgsqlConnection connection,
        MigrationImpactDefinition impact,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.set_config('lock_timeout',$1,false),
                   pg_catalog.set_config('statement_timeout',$2,false)
            """, connection);
        command.Parameters.AddWithValue($"{impact.Document.Budgets.LockTimeoutMilliseconds}ms");
        command.Parameters.AddWithValue($"{impact.Document.Budgets.StatementTimeoutMilliseconds}ms");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TryRestoreSessionTimeoutsAsync(
        NpgsqlConnection connection,
        string lockTimeout,
        string statementTimeout,
        CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open) return;
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT pg_catalog.set_config('lock_timeout',$1,false),
                       pg_catalog.set_config('statement_timeout',$2,false)
                """, connection);
            command.Parameters.AddWithValue(lockTimeout);
            command.Parameters.AddWithValue(statementTimeout);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Session close is the authoritative reset fallback.
        }
    }

    public static async Task VerifyPostconditionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationImpactDefinition impact,
        CancellationToken cancellationToken)
    {
        foreach (var postcondition in impact.Document.Postconditions)
        {
            var passed = postcondition.Kind switch
            {
                "relation-exists" => await ScalarAsync<bool>(
                    connection, "SELECT pg_catalog.to_regclass($1) IS NOT NULL",
                    transaction, cancellationToken, postcondition.Relation),
                "column-no-null" => await ColumnContainsNoNullAsync(
                    connection, transaction, postcondition, cancellationToken),
                "index-valid" => await ScalarAsync<bool>(connection, """
                    SELECT count(*)=1 AND bool_and(index.indisvalid AND index.indisready)
                      FROM pg_catalog.pg_index index
                     WHERE index.indrelid=pg_catalog.to_regclass($1)
                       AND index.indexrelid=pg_catalog.to_regclass($2)
                    """, transaction, cancellationToken,
                    postcondition.Relation, $"public.{postcondition.Index}"),
                _ => false,
            };
            if (!passed)
                throw new MigratorRejectedException("migration_impact_postcondition_failed");
        }
    }

    private static async Task VerifyTerminalPredecessorAsync(
        NpgsqlConnection connection,
        MigrationImpactTarget target,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND bool_and(checksum=$2 AND state IN ('succeeded','skipped_optional'))
              FROM public.schema_migrations
             WHERE version=$1
            """, connection);
        command.Parameters.AddWithValue(target.RequiredPredecessorVersion);
        command.Parameters.AddWithValue(target.RequiredPredecessorSha256);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new MigratorRejectedException("migration_impact_predecessor_not_terminal");
    }

    private static async Task<RelationMetrics> ReadRelationMetricsAsync(
        NpgsqlConnection connection,
        MigrationImpactRelation relation,
        CancellationToken cancellationToken)
    {
        await using var root = new NpgsqlCommand("""
            SELECT relation.oid::bigint,
                   coalesce(tablespace.spcname,'pg_default'),
                   pg_catalog.pg_total_relation_size(relation.oid)::bigint,
                   relation.relkind IN ('r','p')
              FROM pg_catalog.pg_class relation
              JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
              LEFT JOIN pg_catalog.pg_tablespace tablespace ON tablespace.oid=relation.reltablespace
             WHERE namespace.nspname=pg_catalog.split_part($1,'.',1)
               AND relation.relname=pg_catalog.split_part($1,'.',2)
            """, connection);
        root.Parameters.AddWithValue(relation.Name);
        await using var reader = await root.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(3))
            throw new MigratorRejectedException("migration_impact_relation_missing", relation.Name);
        var rootOid = reader.GetInt64(0);
        var actualTablespace = reader.GetString(1);
        var rootBytes = reader.GetInt64(2);
        if (await reader.ReadAsync(cancellationToken) ||
            !string.Equals(actualTablespace, relation.Tablespace, StringComparison.Ordinal))
            throw new MigratorRejectedException(
                "migration_impact_tablespace_contract_mismatch", relation.Name);
        await reader.DisposeAsync();

        var relationOids = new List<long> { rootOid };
        long chunkBytes = 0;
        long compressedBytes = 0;
        if (relation.IncludeChunks)
        {
            await using var chunks = new NpgsqlCommand("""
                SELECT relation.oid::bigint,
                       pg_catalog.pg_total_relation_size(relation.oid)::bigint,
                       chunk.compressed_chunk_id IS NOT NULL
                  FROM _timescaledb_catalog.hypertable hypertable
                  JOIN _timescaledb_catalog.chunk chunk ON chunk.hypertable_id=hypertable.id
                  JOIN pg_catalog.pg_namespace namespace ON namespace.nspname=chunk.schema_name
                  JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace=namespace.oid AND relation.relname=chunk.table_name
                 WHERE hypertable.schema_name=pg_catalog.split_part($1,'.',1)
                   AND hypertable.table_name=pg_catalog.split_part($1,'.',2)
                   AND NOT chunk.dropped
                """, connection);
            chunks.Parameters.AddWithValue(relation.Name);
            await using var chunkReader = await chunks.ExecuteReaderAsync(cancellationToken);
            while (await chunkReader.ReadAsync(cancellationToken))
            {
                relationOids.Add(chunkReader.GetInt64(0));
                chunkBytes = checked(chunkBytes + chunkReader.GetInt64(1));
            }
        }
        if (relation.IncludeCompressed)
        {
            await using var compressed = new NpgsqlCommand("""
                SELECT relation.oid::bigint,
                       pg_catalog.pg_total_relation_size(relation.oid)::bigint
                  FROM _timescaledb_catalog.hypertable hypertable
                  JOIN _timescaledb_catalog.chunk source ON source.hypertable_id=hypertable.id
                  JOIN _timescaledb_catalog.chunk target ON target.id=source.compressed_chunk_id
                  JOIN pg_catalog.pg_namespace namespace ON namespace.nspname=target.schema_name
                  JOIN pg_catalog.pg_class relation
                    ON relation.relnamespace=namespace.oid AND relation.relname=target.table_name
                 WHERE hypertable.schema_name=pg_catalog.split_part($1,'.',1)
                   AND hypertable.table_name=pg_catalog.split_part($1,'.',2)
                   AND NOT source.dropped AND NOT target.dropped
                """, connection);
            compressed.Parameters.AddWithValue(relation.Name);
            await using var compressedReader = await compressed.ExecuteReaderAsync(cancellationToken);
            while (await compressedReader.ReadAsync(cancellationToken))
            {
                relationOids.Add(compressedReader.GetInt64(0));
                compressedBytes = checked(compressedBytes + compressedReader.GetInt64(1));
            }
        }
        return new RelationMetrics(
            relation.Name, actualTablespace, rootBytes, chunkBytes,
            compressedBytes, relationOids);
    }

    private static async Task<LockMetrics> ReadLockMetricsAsync(
        NpgsqlConnection connection,
        long[] relationOids,
        CancellationToken cancellationToken)
    {
        if (relationOids.Length == 0) return new LockMetrics(0, 0);
        await using var command = new NpgsqlCommand("""
            WITH target_sessions AS (
                SELECT DISTINCT lock.pid,lock.granted
                  FROM pg_catalog.pg_locks lock
                 WHERE lock.relation::bigint=ANY($1::bigint[])
                   AND lock.pid IS NOT NULL AND lock.pid<>pg_catalog.pg_backend_pid()),
            session_metrics AS (
                SELECT target_sessions.granted,activity.xact_start
                  FROM target_sessions
                  JOIN pg_catalog.pg_stat_activity activity ON activity.pid=target_sessions.pid)
            SELECT count(*) FILTER (WHERE NOT granted)::integer,
                   coalesce(max(extract(epoch FROM
                       (pg_catalog.clock_timestamp()-xact_start))),0)::double precision
              FROM session_metrics
            """, connection);
        command.Parameters.AddWithValue(relationOids);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_impact_lock_probe_failed");
        return new LockMetrics(reader.GetInt32(0), reader.GetDouble(1));
    }

    private static async Task<ReplicationMetrics> ReadReplicationMetricsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE state='streaming')::integer,
                   coalesce(max(pg_catalog.pg_wal_lsn_diff(
                       pg_catalog.pg_current_wal_lsn(),
                       coalesce(replay_lsn,flush_lsn,write_lsn,sent_lsn))),0)::bigint,
                   bool_and(pid IS NOT NULL AND usename IS NOT NULL)
              FROM pg_catalog.pg_stat_replication
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_impact_replica_probe_failed");
        return new ReplicationMetrics(
            reader.GetInt32(0), reader.GetInt64(1), reader.IsDBNull(2) || reader.GetBoolean(2));
    }

    private static async Task<SlotMetrics> ReadSlotMetricsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(max(pg_catalog.pg_wal_lsn_diff(
                       pg_catalog.pg_current_wal_lsn(),restart_lsn)),0)::bigint,
                   count(*) FILTER (WHERE NOT active)::integer,
                   bool_and(slot_name IS NOT NULL AND slot_type IS NOT NULL)
              FROM pg_catalog.pg_replication_slots
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_impact_slot_probe_failed");
        return new SlotMetrics(
            reader.GetInt64(0), reader.GetInt32(1), reader.IsDBNull(2) || reader.GetBoolean(2));
    }

    private static async Task<bool> ColumnContainsNoNullAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationImpactPostcondition postcondition,
        CancellationToken cancellationToken)
    {
        var (schema, table) = SplitRelation(postcondition.Relation);
        var sql = $"SELECT NOT EXISTS (SELECT 1 FROM {Quote(schema)}.{Quote(table)} " +
                  $"WHERE {Quote(postcondition.Column!)} IS NULL LIMIT 1)";
        return await ScalarAsync<bool>(connection, sql, transaction, cancellationToken);
    }

    internal static (string Schema, string Table) SplitRelation(string relation)
    {
        var parts = relation.Split('.', StringSplitOptions.None);
        if (parts is not [var schema, var table])
            throw new MigratorRejectedException("migration_impact_relation_invalid");
        return (schema, table);
    }

    internal static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params object[] values) =>
        await ScalarAsync<T>(connection, sql, transaction: null, cancellationToken, values);

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken,
        params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        for (var index = 0; index < values.Length; index++)
            command.Parameters.AddWithValue(values[index]);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            throw new MigratorRejectedException("migration_impact_probe_missing");
        return (T)value;
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record RelationMetrics(
        string Relation,
        string Tablespace,
        long RootBytes,
        long ChunkBytes,
        long CompressedBytes,
        IReadOnlyList<long> RelationOids);
    private sealed record LockMetrics(int WaitingLocks, double OldestTransactionSeconds);
    private sealed record ReplicationMetrics(int StreamingReplicas, long MaximumLagBytes, bool Visible);
    private sealed record SlotMetrics(long MaximumRetentionBytes, int InactiveSlots, bool Visible);
}
