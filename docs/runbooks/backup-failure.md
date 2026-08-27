# Backup failure, stale recovery point and PITR

Triggers: missing/stale backup metric, backup failure, host/storage loss.

Objectives: latest recoverable transaction no older than 15 minutes; service restored
within 120 minutes. These objectives require encrypted off-host base backup plus WAL,
not only a VM snapshot or logical dump.

1. Stop promotion and record the last successful base backup, contiguous WAL range,
   object checksum, KMS key version and release/migration trust-root metadata.
2. Never test restore against the production volume. Create an isolated resource/project
   identity, network, volume and secret set; verify that the restored database preserves
   the signed production deployment and database-system identities.
3. Verify object immutability/checksum and KMS decrypt authorization, restore the base,
   replay WAL to the selected target timestamp, then stop recovery.
4. Run RoleBootstrap verification, migrator verify-only, terminal migration/trust-root
   check, hypertable/compression checks, DQA and API smoke with isolated managed logins.
5. Measure actual recovery point and elapsed restore time. Destroy isolated resources
   only after evidence is signed and retained.
6. For production recovery, require incident owner approval and an explicit DNS/cutover
   plan; never reuse test credentials or silently weaken role separation.

Do not delete the previous backup chain until two successful new cycles and one measured
restore exist. A missing WAL segment makes the chain unrecoverable and must alert.
## Runtime checks

The encrypted repository policy is RPO 15 minutes, RTO 120 minutes, WAL retention 14
days, weekly bases for 8 weeks, and monthly bases for 12 months. `database-wal-archive`
and the `database-backup` scheduler must remain running; a verified base cycle must
complete daily.

1. Check `saydin_backup_last_success_timestamp_seconds` separately for `wal` and `base`.
   Check `saydin_backup_wal_spool_free_bytes` against
   `saydin_backup_wal_spool_capacity_floor_bytes`; never delete spool segments to clear
   a capacity page before a verified replacement chain exists.
   Preserve the container logs and release manifest; do not print restic or database
   credentials while diagnosing.
2. Confirm the workload-identity token file is current, the object-store role still has
   access only to the configured repository prefix, and the KMS-materialized restic
   password file remains private. Do not replace either with an environment secret.
3. Confirm the dedicated login still has only `LOGIN REPLICATION` and cannot read public
   application tables. Never substitute the bootstrap administrator, migrator, API, or
   ingestion login.
4. Once object storage is healthy, restart the WAL archiver. Force a base backup only
   after WAL continuity is re-established. Retain the failed spool and evidence until
   the incident owner approves disposal.
   Check `<role-prefix>_backup_slot_v1`: if the fixed 8 GB retained-WAL bound was
   exceeded or the slot is invalid, treat the old chain as broken rather than silently
   skipping WAL.
5. Run the isolated restore workflow at a target before and after the gap. Escalate if
   either target fails or the observed recovery point exceeds 15 minutes.

For a controlled isolated run, render the signed release environment exactly as the
restore workflow does, then invoke the seven-argument primitive (all paths absolute):

```sh
python3 infrastructure/release/render-deployment-env.py \
  --base "$SAYDIN_OPERATOR_ENV" --manifest "$SAYDIN_RELEASE_MANIFEST" \
  --output "$SAYDIN_RESTORE_ENV"
infrastructure/backup/restore-drill.sh "$SAYDIN_RUN_ID" "$SAYDIN_RUN_ATTEMPT" \
  "$SAYDIN_RECOVERY_TARGET_UTC" "$SAYDIN_RELEASE_MANIFEST" \
  "$SAYDIN_RESTORE_ENV" "$SAYDIN_RESTORE_CONTRACT" "$SAYDIN_EVIDENCE_DIR"
```

The primitive creates only `saydin-restore-<run>-<attempt>-*` resources and restores
through the guarded `/restore-drill/work` leaf inside its disposable fetch container.
It is not a production cutover command. Production recovery requires the incident
owner's approved destination volume, DNS/cutover plan and separately reviewed command
record; do not weaken `DISPOSABLE_RESTORE_ONLY` or point the drill at a production
volume.

Interpret the bounded runtime codes before restarting:

- `backup_wal_highwater_probe_deferred`, `_unavailable`, or
  `backup_wal_receiver_not_caught_up`: only unverified encrypted WAL snapshots may be
  progressing; the verified freshness metric intentionally remains stale.
- `backup_wal_spool_capacity_insufficient`: provision/expand the exact external WAL
  volume; do not truncate the recovery chain.
- `backup_physical_probe_lock_timeout` or `backup_base_staging_capacity_insufficient`:
  preserve both lanes' logs and resolve the lock/disk boundary before retry.
- `backup_repository_prune_deferred`: hot backup writes may remain valid, but record
  repository growth and rerun prune only after a successful base cycle.
- `restore_wal_recovery_point_stale`: the selected snapshot cannot prove the 15-minute
  RPO; keep the incident open even if PostgreSQL starts.

The production profile is admitted only after the managed HBA block verifies exact
`hostssl replication` plus adjacent IPv4/IPv6 ordinary-SQL rejects, phase-aware
post-bootstrap reports no pending backup role, and real base/WAL protocol acceptance
passes. Any drift is a release and incident blocker; never widen the CIDR or add an SQL
allow rule while restoring service.
