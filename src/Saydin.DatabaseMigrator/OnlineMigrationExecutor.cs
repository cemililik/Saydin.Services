using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Saydin.DatabaseMigrator;

internal sealed class OnlineMigrationExecutor(
    TextWriter output,
    IMigrationFaultInjector faultInjector,
    string ownerRole,
    string timescaleSchedulerRole)
{
    public async Task<string> ExecuteAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        CancellationToken cancellationToken)
    {
        var plan = impact.Document.OnlinePlan ??
                   throw new MigratorRejectedException("migration_online_plan_contract_invalid");
        await EnsureCheckpointTableAsync(connection, cancellationToken);
        var leaseNonce = Guid.CreateVersion7();
        await InitializeCheckpointAndPolicyAsync(
            connection, migration, impact, plan, leaseNonce, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(impact.Document.Budgets.TotalTimeoutSeconds))
                    throw new MigratorRejectedException("migration_online_total_budget_exceeded");
                var completed = await ExecuteBatchAsync(
                    connection, migration, impact, plan, leaseNonce, cancellationToken);
                if (completed)
                {
                    await output.WriteLineAsync($"applied online: {migration.FileName}");
                    return "succeeded";
                }
            }
        }
        catch
        {
            await TryRestoreCompressionPolicyAsync(
                connection, migration.Version, impact.ManifestSha256, cancellationToken);
            throw;
        }
    }

    private async Task EnsureCheckpointTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS public.saydin_online_migration_checkpoints (
                migration_version text CONSTRAINT pk_saydin_online_migration_checkpoints PRIMARY KEY,
                manifest_sha256 text NOT NULL CONSTRAINT ck_saydin_online_manifest_sha256
                    CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
                plan_kind text NOT NULL CONSTRAINT ck_saydin_online_plan_kind
                    CHECK (plan_kind='uuid-keyset-set-constant-where-null'),
                state text NOT NULL CONSTRAINT ck_saydin_online_state
                    CHECK (state IN ('running','succeeded')),
                last_key uuid NULL,
                processed_rows bigint NOT NULL CONSTRAINT ck_saydin_online_processed_rows
                    CHECK (processed_rows>=0),
                lease_nonce uuid NOT NULL,
                lease_expires_at timestamptz NOT NULL,
                compression_job_id integer NULL,
                compression_was_scheduled boolean NULL,
                updated_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
                CONSTRAINT ck_saydin_online_compression_pair
                    CHECK ((compression_job_id IS NULL)=(compression_was_scheduled IS NULL))
            );
            REVOKE ALL ON public.saydin_online_migration_checkpoints FROM PUBLIC;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await VerifyCheckpointTableAsync(connection, cancellationToken);
    }

    private async Task VerifyCheckpointTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var relation = new NpgsqlCommand("""
            WITH target AS (
                SELECT relation.oid,relation.relowner,relation.relkind,relation.relpersistence,
                       relation.relrowsecurity,relation.relforcerowsecurity,
                       pg_catalog.pg_get_userbyid(relation.relowner) AS owner_name,
                       relation.relacl
                  FROM pg_catalog.pg_class relation
                 WHERE relation.oid=
                       pg_catalog.to_regclass('public.saydin_online_migration_checkpoints')),
            owner_acl AS (
                SELECT acl.privilege_type,acl.is_grantable,acl.grantor,acl.grantee,target.relowner
                  FROM target
                  CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(
                      target.relacl,pg_catalog.acldefault('r',target.relowner))) acl)
            SELECT count(*)=1
               AND bool_and(owner_name=$1 AND relkind='r' AND relpersistence='p'
                            AND NOT relrowsecurity AND NOT relforcerowsecurity)
               AND NOT EXISTS (
                   SELECT 1 FROM pg_catalog.pg_trigger catalog_trigger,target
                    WHERE catalog_trigger.tgrelid=target.oid AND NOT catalog_trigger.tgisinternal)
               AND NOT EXISTS (SELECT 1 FROM owner_acl WHERE grantee<>relowner)
               AND (SELECT count(*)=7 AND bool_and(
                        grantee=relowner AND grantor=relowner AND NOT is_grantable)
                      FROM owner_acl WHERE grantee=relowner)
              FROM target
            """, connection))
        {
            relation.Parameters.AddWithValue(ownerRole);
            if (await relation.ExecuteScalarAsync(cancellationToken) is not true)
                throw new MigratorRejectedException("migration_online_checkpoint_contract_mismatch");
        }

        await using (var columns = new NpgsqlCommand("""
            SELECT pg_catalog.string_agg(
                       attribute.attname||'|'||
                       pg_catalog.format_type(attribute.atttypid,attribute.atttypmod)||'|'||
                       attribute.attnotnull::text||'|'||
                       coalesce(pg_catalog.pg_get_expr(default_value.adbin,default_value.adrelid),''),
                       E'\n' ORDER BY attribute.attnum)
              FROM pg_catalog.pg_attribute attribute
              LEFT JOIN pg_catalog.pg_attrdef default_value
                ON default_value.adrelid=attribute.attrelid
               AND default_value.adnum=attribute.attnum
             WHERE attribute.attrelid=
                   'public.saydin_online_migration_checkpoints'::pg_catalog.regclass
               AND attribute.attnum>0 AND NOT attribute.attisdropped
            """, connection))
        {
            var actual = Convert.ToString(await columns.ExecuteScalarAsync(cancellationToken));
            const string expected = """
                migration_version|text|true|
                manifest_sha256|text|true|
                plan_kind|text|true|
                state|text|true|
                last_key|uuid|false|
                processed_rows|bigint|true|
                lease_nonce|uuid|true|
                lease_expires_at|timestamp with time zone|true|
                compression_job_id|integer|false|
                compression_was_scheduled|boolean|false|
                updated_at|timestamp with time zone|true|clock_timestamp()
                """;
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new MigratorRejectedException("migration_online_checkpoint_contract_mismatch");
        }

        await using (var constraints = new NpgsqlCommand("""
            SELECT pg_catalog.string_agg(
                       catalog_constraint.conname||'|'||
                       pg_catalog.pg_get_constraintdef(catalog_constraint.oid,false),
                       E'\n' ORDER BY catalog_constraint.conname COLLATE "C")
              FROM pg_catalog.pg_constraint catalog_constraint
             WHERE catalog_constraint.conrelid=
                   'public.saydin_online_migration_checkpoints'::pg_catalog.regclass
            """, connection))
        {
            var actual = Convert.ToString(await constraints.ExecuteScalarAsync(cancellationToken));
            const string expected = """
                ck_saydin_online_compression_pair|CHECK (((compression_job_id IS NULL) = (compression_was_scheduled IS NULL)))
                ck_saydin_online_manifest_sha256|CHECK ((manifest_sha256 ~ '^[0-9a-f]{64}$'::text))
                ck_saydin_online_plan_kind|CHECK ((plan_kind = 'uuid-keyset-set-constant-where-null'::text))
                ck_saydin_online_processed_rows|CHECK ((processed_rows >= 0))
                ck_saydin_online_state|CHECK ((state = ANY (ARRAY['running'::text, 'succeeded'::text])))
                pk_saydin_online_migration_checkpoints|PRIMARY KEY (migration_version)
                """;
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new MigratorRejectedException("migration_online_checkpoint_contract_mismatch");
        }
    }

    private async Task InitializeCheckpointAndPolicyAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        MigrationOnlinePlan plan,
        Guid leaseNonce,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await SetLocalContractAsync(connection, transaction, impact, cancellationToken);
        int? compressionJobId = null;
        bool? compressionWasScheduled = null;
        if (plan.PauseCompressionPolicy)
        {
            await SetLocalRoleAsync(
                connection, transaction, timescaleSchedulerRole, cancellationToken);
            await using var inspect = new NpgsqlCommand("""
                SELECT job_id::integer,scheduled
                  FROM timescaledb_information.jobs
                 WHERE hypertable_schema=pg_catalog.split_part($1,'.',1)
                   AND hypertable_name=pg_catalog.split_part($1,'.',2)
                   AND proc_name='policy_compression'
                 ORDER BY job_id
                """, connection, transaction);
            inspect.Parameters.AddWithValue(plan.Relation);
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new MigratorRejectedException("migration_online_compression_policy_missing");
            compressionJobId = reader.GetInt32(0);
            compressionWasScheduled = reader.GetBoolean(1);
            if (await reader.ReadAsync(cancellationToken))
                throw new MigratorRejectedException("migration_online_compression_policy_ambiguous");
            await reader.DisposeAsync();
            if (compressionWasScheduled.Value)
                await SetCompressionJobScheduledAsync(
                    connection, transaction, compressionJobId.Value, false, cancellationToken);
            await SetLocalRoleAsync(connection, transaction, ownerRole, cancellationToken);
        }

        await using (var checkpoint = new NpgsqlCommand("""
            INSERT INTO public.saydin_online_migration_checkpoints
                (migration_version,manifest_sha256,plan_kind,state,last_key,processed_rows,
                 lease_nonce,lease_expires_at,compression_job_id,compression_was_scheduled,updated_at)
            VALUES ($1,$2,$3,'running',NULL,0,$4,
                    pg_catalog.clock_timestamp()+pg_catalog.make_interval(secs=>$5),$6,$7,
                    pg_catalog.clock_timestamp())
            ON CONFLICT (migration_version) DO UPDATE
            SET lease_nonce=EXCLUDED.lease_nonce,
                lease_expires_at=EXCLUDED.lease_expires_at,
                updated_at=pg_catalog.clock_timestamp()
            WHERE saydin_online_migration_checkpoints.manifest_sha256=EXCLUDED.manifest_sha256
              AND saydin_online_migration_checkpoints.plan_kind=EXCLUDED.plan_kind
              AND saydin_online_migration_checkpoints.state='running'
            """, connection, transaction))
        {
            checkpoint.Parameters.AddWithValue(migration.Version);
            checkpoint.Parameters.AddWithValue(impact.ManifestSha256);
            checkpoint.Parameters.AddWithValue(plan.PlanKind);
            checkpoint.Parameters.AddWithValue(leaseNonce);
            checkpoint.Parameters.AddWithValue(LeaseSeconds(impact));
            checkpoint.Parameters.AddWithValue((object?)compressionJobId ?? DBNull.Value);
            checkpoint.Parameters.AddWithValue((object?)compressionWasScheduled ?? DBNull.Value);
            var affected = await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
                throw new MigratorRejectedException("migration_online_checkpoint_contract_mismatch");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> ExecuteBatchAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        MigrationOnlinePlan plan,
        Guid leaseNonce,
        CancellationToken cancellationToken)
    {
        Guid? expectedCursor = null;
        long expectedProcessed = 0;
        Guid? nextCursor = null;
        long selected = 0;
        var commitAttempted = false;
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await SetLocalContractAsync(connection, transaction, impact, cancellationToken);
            await using (var checkpoint = new NpgsqlCommand("""
                SELECT last_key,processed_rows,state,lease_nonce
                  FROM public.saydin_online_migration_checkpoints
                 WHERE migration_version=$1 AND manifest_sha256=$2 AND plan_kind=$3
                 FOR UPDATE
                """, connection, transaction))
            {
                checkpoint.Parameters.AddWithValue(migration.Version);
                checkpoint.Parameters.AddWithValue(impact.ManifestSha256);
                checkpoint.Parameters.AddWithValue(plan.PlanKind);
                await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new MigratorRejectedException("migration_online_checkpoint_missing");
                expectedCursor = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                expectedProcessed = reader.GetInt64(1);
                var state = reader.GetString(2);
                if (state == "succeeded") return true;
                if (state != "running" || await reader.ReadAsync(cancellationToken))
                    throw new MigratorRejectedException("migration_online_checkpoint_contract_mismatch");
            }

            var batch = await ExecuteGeneratedBatchAsync(
                connection, transaction, plan, expectedCursor,
                plan.PauseCompressionPolicy ? timescaleSchedulerRole : null,
                cancellationToken);
            selected = batch.Selected;
            if (batch.Selected != batch.Updated || batch.Selected < 0 ||
                batch.Selected > plan.BatchSize || batch.Selected > 0 && batch.MaximumKey is null)
                throw new MigratorRejectedException("migration_online_batch_cas_mismatch");
            nextCursor = batch.MaximumKey;
            if (selected == 0)
            {
                await VerifyDerivedPostconditionAsync(
                    connection, transaction, plan, cancellationToken);
                await MigrationImpactPreflight.VerifyPostconditionsAsync(
                    connection, transaction, impact, cancellationToken);
                if (plan.PauseCompressionPolicy)
                    await SetLocalRoleAsync(connection, transaction, ownerRole, cancellationToken);
                await RestoreCompressionPolicyInTransactionAsync(
                    connection, transaction, migration.Version,
                    impact.ManifestSha256, cancellationToken);
                await MarkSucceededAsync(
                    connection, transaction, migration, impact, expectedCursor,
                    expectedProcessed, leaseNonce, cancellationToken);
            }
            else
            {
                if (plan.PauseCompressionPolicy)
                    await SetLocalRoleAsync(connection, transaction, ownerRole, cancellationToken);
                await UpdateCheckpointAsync(
                    connection, transaction, migration.Version, impact.ManifestSha256,
                    expectedCursor, nextCursor!.Value, expectedProcessed, selected,
                    leaseNonce, LeaseSeconds(impact), cancellationToken);
            }

            await faultInjector.AfterBodyAsync(
                migration, connection, transaction, cancellationToken);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            await faultInjector.AfterCommitAsync(migration, cancellationToken);
            return selected == 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (commitAttempted && await ReconcileBatchCommitAsync(
                    connection, migration, impact, expectedCursor, nextCursor,
                    expectedProcessed, selected, cancellationToken))
                return selected == 0;
            throw;
        }
    }

    private static async Task<OnlineBatchResult> ExecuteGeneratedBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationOnlinePlan plan,
        Guid? cursor,
        string? executionRole,
        CancellationToken cancellationToken)
    {
        if (executionRole is not null)
            await SetLocalRoleAsync(connection, transaction, executionRole, cancellationToken);
        var (schema, table) = MigrationImpactPreflight.SplitRelation(plan.Relation);
        var relation = $"{MigrationImpactPreflight.Quote(schema)}.{MigrationImpactPreflight.Quote(table)}";
        var key = MigrationImpactPreflight.Quote(plan.KeyColumn);
        var target = MigrationImpactPreflight.Quote(plan.TargetColumn);
        var sql = $"""
            WITH batch AS MATERIALIZED (
                SELECT {key}
                  FROM {relation}
                 WHERE ($1::uuid IS NULL OR {key}>$1) AND {target} IS NULL
                 ORDER BY {key}
                 LIMIT $2
            ), updated AS (
                UPDATE {relation} target_relation
                   SET {target}=$3::{plan.TargetType}
                  FROM batch
                 WHERE target_relation.{key}=batch.{key}
                   AND target_relation.{target} IS NULL
                RETURNING target_relation.{key}
            )
            SELECT (SELECT count(*) FROM batch)::bigint,
                   (SELECT count(*) FROM updated)::bigint,
                   (SELECT {key} FROM batch ORDER BY {key} DESC LIMIT 1)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = Math.Max(1, (int)Math.Ceiling(plan.MaxBatchMilliseconds / 1_000d)),
        };
        command.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = string.Empty,
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = (object?)cursor ?? DBNull.Value,
        });
        command.Parameters.AddWithValue(plan.BatchSize);
        command.Parameters.AddWithValue(OnlineTargetValue(plan));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_online_batch_result_missing");
        var result = new OnlineBatchResult(
            reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetGuid(2));
        if (await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_online_batch_result_ambiguous");
        return result;
    }

    private static object OnlineTargetValue(MigrationOnlinePlan plan) => plan.TargetType switch
    {
        "boolean" when plan.TargetValue.ValueKind is JsonValueKind.True or JsonValueKind.False =>
            plan.TargetValue.GetBoolean(),
        "smallint" when plan.TargetValue.TryGetInt16(out var value) => value,
        "integer" when plan.TargetValue.TryGetInt32(out var value) => value,
        "bigint" when plan.TargetValue.TryGetInt64(out var value) => value,
        "text" when plan.TargetValue.ValueKind == JsonValueKind.String =>
            plan.TargetValue.GetString() ?? string.Empty,
        "uuid" when plan.TargetValue.ValueKind == JsonValueKind.String &&
                    Guid.TryParseExact(plan.TargetValue.GetString(), "D", out var value) => value,
        _ => throw new MigratorRejectedException("migration_online_target_value_invalid"),
    };

    private static async Task UpdateCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string version,
        string manifestSha256,
        Guid? expectedCursor,
        Guid nextCursor,
        long expectedProcessed,
        long selected,
        Guid leaseNonce,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public.saydin_online_migration_checkpoints
               SET last_key=$1,processed_rows=processed_rows+$2,lease_nonce=$3,
                   lease_expires_at=pg_catalog.clock_timestamp()+pg_catalog.make_interval(secs=>$4),
                   updated_at=pg_catalog.clock_timestamp()
             WHERE migration_version=$5 AND manifest_sha256=$6 AND state='running'
               AND last_key IS NOT DISTINCT FROM $7 AND processed_rows=$8
            """, connection, transaction);
        command.Parameters.AddWithValue(nextCursor);
        command.Parameters.AddWithValue(selected);
        command.Parameters.AddWithValue(leaseNonce);
        command.Parameters.AddWithValue(leaseSeconds);
        command.Parameters.AddWithValue(version);
        command.Parameters.AddWithValue(manifestSha256);
        command.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = string.Empty,
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = (object?)expectedCursor ?? DBNull.Value,
        });
        command.Parameters.AddWithValue(expectedProcessed);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new MigratorRejectedException("migration_online_checkpoint_cas_failed");
    }

    private static async Task MarkSucceededAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        Guid? expectedCursor,
        long expectedProcessed,
        Guid leaseNonce,
        CancellationToken cancellationToken)
    {
        await using (var checkpoint = new NpgsqlCommand("""
            UPDATE public.saydin_online_migration_checkpoints
               SET state='succeeded',lease_nonce=$1,lease_expires_at=pg_catalog.clock_timestamp(),
                   updated_at=pg_catalog.clock_timestamp()
             WHERE migration_version=$2 AND manifest_sha256=$3 AND state='running'
               AND last_key IS NOT DISTINCT FROM $4 AND processed_rows=$5
            """, connection, transaction))
        {
            checkpoint.Parameters.AddWithValue(leaseNonce);
            checkpoint.Parameters.AddWithValue(migration.Version);
            checkpoint.Parameters.AddWithValue(impact.ManifestSha256);
            checkpoint.Parameters.Add(new NpgsqlParameter
            {
                ParameterName = string.Empty,
                NpgsqlDbType = NpgsqlDbType.Uuid,
                Value = (object?)expectedCursor ?? DBNull.Value,
            });
            checkpoint.Parameters.AddWithValue(expectedProcessed);
            if (await checkpoint.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new MigratorRejectedException("migration_online_checkpoint_cas_failed");
        }
        await using var terminal = new NpgsqlCommand("""
            UPDATE public.schema_migrations
               SET checksum=$1,state='succeeded',error_code=NULL,
                   applied_at=pg_catalog.clock_timestamp(),completed_at=pg_catalog.clock_timestamp()
             WHERE version=$2 AND checksum=$1 AND state='running'
            """, connection, transaction);
        terminal.Parameters.AddWithValue(migration.Checksum);
        terminal.Parameters.AddWithValue(migration.Version);
        if (await terminal.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new MigratorRejectedException("migration_tracking_row_missing");
    }

    private static async Task VerifyDerivedPostconditionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationOnlinePlan plan,
        CancellationToken cancellationToken)
    {
        var (schema, table) = MigrationImpactPreflight.SplitRelation(plan.Relation);
        var sql = $"SELECT NOT EXISTS (SELECT 1 FROM " +
                  $"{MigrationImpactPreflight.Quote(schema)}.{MigrationImpactPreflight.Quote(table)} " +
                  $"WHERE {MigrationImpactPreflight.Quote(plan.TargetColumn)} IS NULL LIMIT 1)";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new MigratorRejectedException("migration_online_postcondition_failed");
    }

    private static async Task<bool> ReconcileBatchCommitAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        MigrationImpactDefinition impact,
        Guid? expectedCursor,
        Guid? nextCursor,
        long expectedProcessed,
        long selected,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open) return false;
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT state,last_key,processed_rows
                  FROM public.saydin_online_migration_checkpoints
                 WHERE migration_version=$1 AND manifest_sha256=$2
                """, connection);
            command.Parameters.AddWithValue(migration.Version);
            command.Parameters.AddWithValue(impact.ManifestSha256);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            var state = reader.GetString(0);
            Guid? cursor = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            var processed = reader.GetInt64(2);
            return selected == 0
                ? state == "succeeded" && cursor == expectedCursor && processed == expectedProcessed
                : state == "running" && cursor == nextCursor &&
                  processed == checked(expectedProcessed + selected);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RestoreCompressionPolicyInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string version,
        string manifestSha256,
        CancellationToken cancellationToken)
    {
        await using var inspect = new NpgsqlCommand("""
            SELECT compression_job_id,compression_was_scheduled
              FROM public.saydin_online_migration_checkpoints
             WHERE migration_version=$1 AND manifest_sha256=$2
             FOR UPDATE
            """, connection, transaction);
        inspect.Parameters.AddWithValue(version);
        inspect.Parameters.AddWithValue(manifestSha256);
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_online_checkpoint_missing");
        int? jobId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
        bool? scheduled = reader.IsDBNull(1) ? null : reader.GetBoolean(1);
        if (await reader.ReadAsync(cancellationToken))
            throw new MigratorRejectedException("migration_online_checkpoint_contract_mismatch");
        await reader.DisposeAsync();
        if (jobId.HasValue && scheduled == true)
        {
            await SetLocalRoleAsync(
                connection, transaction, timescaleSchedulerRole, cancellationToken);
            await SetCompressionJobScheduledAsync(
                connection, transaction, jobId.Value, true, cancellationToken);
            await SetLocalRoleAsync(connection, transaction, ownerRole, cancellationToken);
        }
    }

    private async Task TryRestoreCompressionPolicyAsync(
        NpgsqlConnection connection,
        string version,
        string manifestSha256,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open) return;
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await RestoreCompressionPolicyInTransactionAsync(
                connection, transaction, version, manifestSha256, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The durable checkpoint retains the original policy state. A later
            // invocation must reconcile it before declaring the migration done.
        }
    }

    private static async Task SetCompressionJobScheduledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int jobId,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT public.alter_job($1,scheduled=>$2)", connection, transaction);
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(scheduled);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetLocalRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        CancellationToken cancellationToken)
    {
        string statement;
        await using (var format = new NpgsqlCommand(
                         "SELECT pg_catalog.format('SET LOCAL ROLE %I',$1)",
                         connection, transaction))
        {
            format.Parameters.AddWithValue(role);
            statement = Convert.ToString(await format.ExecuteScalarAsync(cancellationToken)) ??
                        throw new MigratorRejectedException("migration_online_role_transition_failed");
        }
        await using var command = new NpgsqlCommand(statement, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetLocalContractAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationImpactDefinition impact,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.set_config('lock_timeout',$1,true),
                   pg_catalog.set_config('statement_timeout',$2,true),
                   pg_catalog.set_config('search_path','public,pg_temp',true)
            """, connection, transaction);
        command.Parameters.AddWithValue($"{impact.Document.Budgets.LockTimeoutMilliseconds}ms");
        command.Parameters.AddWithValue($"{impact.Document.Budgets.StatementTimeoutMilliseconds}ms");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static int LeaseSeconds(MigrationImpactDefinition impact) =>
        Math.Clamp(impact.Document.OnlinePlan!.MaxBatchMilliseconds / 1_000 + 30, 30, 330);

    private sealed record OnlineBatchResult(long Selected, long Updated, Guid? MaximumKey);
}
