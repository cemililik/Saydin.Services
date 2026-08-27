# Principal-retention migration 022

## Purpose and hard boundary

Use this procedure only when the target database has not yet completed frozen
migration `022_principal_retention`. Migration 022 runs transactionally, takes an
`ACCESS EXCLUSIVE` lock on `public.users`, and decompresses/recompresses every
compressed `activity_logs` chunk in one statement. It has no online checkpoint and
is not protected by the impact-manifest admission used by later migrations.

Never edit 022, its checksum, `schema_migrations`, or the migration-control row.
Never retry an over-budget rehearsal unchanged. If the measured operation cannot
fit the compiled 3600-second command / 7200-second total maxima and the approved
maintenance window, promotion is blocked until a reviewed forward bootstrap design
is available.

## Read-only assessment

Run these queries with the deployment's managed audit/control identity. Bind the
release's exact database and system-identifier evidence; do not use an interactive
superuser fallback.

```sql
SELECT version, state, checksum
FROM public.schema_migrations
WHERE version = '022_principal_retention';

SELECT count(*) AS compressed_chunks,
       coalesce(sum(before_compression_total_bytes), 0) AS expanded_bytes,
       coalesce(sum(after_compression_total_bytes), 0) AS compressed_bytes
FROM timescaledb_information.hypertable_compression_stats
WHERE hypertable_schema = 'public'
  AND hypertable_name = 'activity_logs';

SELECT count(*) AS activity_chunks
FROM timescaledb_information.chunks
WHERE hypertable_schema = 'public'
  AND hypertable_name = 'activity_logs';
```

If 022 is already `succeeded` with its compiled checksum, stop: use normal release
verification. Otherwise retain the query results, exact release digest, deployment
ID, database identity hash, chunk count, expanded/compressed bytes, current database
size, free storage, WAL retention, replica lag and physical-slot state as the change
record. Any checksum/state mismatch is an incident, not a maintenance task.

## Rehearsal and admission

1. Restore the latest production backup into an isolated cluster with the same
   TimescaleDB image, storage class, CPU/memory limits, role contract and chunk set.
   Prove PITR first and record the restore point.
2. Measure the normal signed-release path through 022. Record wall-clock duration,
   peak database and WAL bytes, temporary expanded bytes, lock duration, replica lag
   and recompressed chunk count. A fresh empty database is not valid evidence.
3. Reserve free database storage for at least the measured peak, the reported
   expanded bytes and a separately approved safety margin. Reserve off-host/WAL
   capacity for the measured peak WAL plus margin. Admission fails if either quota
   cannot be proved; filesystem free space alone is not an allocation authority.
4. Set deployment-specific `--command-timeout-seconds` and
   `--total-timeout-seconds` only from the rehearsal: each must cover the measured
   duration plus the approved margin, remain within 3600/7200 seconds, and remain
   inside the maintenance window. Retain the exact values in the signed change
   record. Do not raise them merely because a production attempt timed out.
5. Obtain independent approval for the outage window, headroom, backup/PITR proof,
   measured timeouts and rollback boundary. The rollback boundary is the start of
   the migration transaction; after commit, recovery is a forward fix.

## Maintenance execution

1. Block new public traffic and stop every old API and ingestion replica. Prove no
   principal, saved-scenario or activity-log writer remains and drain existing
   sessions before starting the migrator. A rolling release with old writers still
   active is not admitted.
2. Start the normal digest-pinned, signed deployment with the approved timeout
   arguments. Do not invoke SQL directly and do not put credentials in argv or an
   environment file. Keep the backup/WAL receiver and monitoring plane running.
3. Observe migration runtime, database/WAL allocation, replica/slot lag, blocked
   sessions and the maintenance deadline. If a pre-approved abort threshold is
   crossed, stop the migrator once, preserve evidence and allow PostgreSQL to roll
   back. Do not kill PostgreSQL, edit tracking state, or loop retries.
4. If rollback or crash exceeds the remaining window, keep traffic blocked and
   escalate. Rehearse a changed plan before another attempt.

## Postconditions

Before restoring traffic, the normal migrator `--verify-only` and role-bootstrap
post-migration verification must both succeed. Also retain evidence that:

- 022 is `succeeded` with its compiled checksum and no migration is `pending`;
- `public.activity_logs` compression is enabled, the compression policy is present,
  and every formerly compressed chunk is recompressed;
- no session is waiting on or holding the migration's `public.users` table lock;
- database/WAL headroom and replica/physical-slot health are back inside bounds;
- principal deletion, saved-scenario access and activity-log writes pass their
  release acceptance probes; and
- API/ingestion replicas are started only after all preceding checks pass.

Attach rehearsal measurements, approvals, runtime metrics, terminal migration
output, verify-only output and backup recovery-point evidence to the release receipt.
Do not attach credentials, row payloads or unbounded catalog dumps.
