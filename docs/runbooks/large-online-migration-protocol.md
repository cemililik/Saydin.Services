# Large and online migration protocol

## Scope and hard boundary

This protocol applies to every migration after the compiled 001–022 trust root. Existing migrations retain their conventional transactional behavior. An unrecognized SQL statement, missing/invalid manifest, wrong target, invalid budget, or unsupported execution plan is rejected before migration-control or schema-tracking mutation.

The current reusable online surface is deliberately narrow: one public relation, a UUID keyset cursor, and a generated parameterized `SET <column>=<constant> WHERE <column> IS NULL` batch. It does not claim to execute arbitrary backfills, concurrent index creation, procedures, multi-table plans, non-UUID cursors, or generic SQL. Those need a new reviewed executor plan kind; they are not operable by changing JSON.

## Release preparation

1. Classify the exact SQL with the runner analyzer. It detects table rewrites, constraint validation, concurrent and nonconcurrent indexes, unbounded UPDATE/DELETE, and Timescale compression/chunk operations. Opaque statements are a blocker.
2. Choose `transactional` only when the static statement set is supported and the target relation is within all signed budgets and the compiled 64 MiB heavy-transaction ceiling. `large-dml` always requires an online plan.
3. Inventory every root relation, chunk, and compressed relation affected. Set `includeChunks`/`includeCompressed`; omission is a release-review failure, not a runtime discovery mechanism.
4. Record the target database, cluster system-identifier SHA-256, immediate predecessor version/SHA, and prefix manifest SHA. A manifest is target-specific.
5. Obtain the tablespace allocation authority's capacity in `declaredTablespaceCapacityBytes`. This is a signed storage-allocation assertion. Runtime free-after is computed as declared capacity minus PostgreSQL `pg_tablespace_size` minus estimated additional bytes; it is not an operating-system filesystem probe. Platform admission must separately ensure the signed capacity does not exceed the volume/quota allocation.
6. Bound lock/statement/total time, additional/WAL bytes, relation/compressed bytes, free bytes/headroom, old blockers/waiters, replica count/lag, and physical slot retention/availability. A manifest may only tighten runner timeouts.
7. Define fixed postconditions: relation exists, index valid/ready, or target column contains no nulls.
8. Canonicalize, sign offline, promote the public SPKI fingerprint independently, and retain the reviewed canonical manifest as release evidence.

## Admission sequence

The runner performs this sequence before any target mutation:

1. Verify the frozen trust-root prefix and the complete future SQL/impact file set.
2. Verify canonical JSON, P-256 signature, public SPKI pin, SQL SHA, predecessor/prefix hashes, and target identity.
3. Open the expected migrator identity, acquire target and migration advisory locks, and prove the managed terminal predecessor.
4. Read relation/chunk/compressed sizes and tablespace consumption; apply capacity/headroom and compiled size limits.
5. Inspect relation waiters and oldest transactions under the signed lock/statement budget.
6. Inspect streaming replica lag and physical replication-slot activity/retention.
7. Only after every check passes, create/update migration tracking state and execute the selected mode.

Preflight failure must leave `saydin_migration_control` ready, omit the future `schema_migrations` row, and omit online checkpoint infrastructure.

## Resumable execution

The executor owns `public.saydin_online_migration_checkpoints`. Each committed batch records the manifest SHA, plan kind, UUID cursor, exact processed-row count, fresh lease nonce, and expiry in the same transaction as target mutations. The selected and updated counts must match. A retry locks the checkpoint, starts strictly after the committed cursor, and cannot silently skip or double-count rows.

If commit acknowledgement is uncertain, the runner reconciles the cursor/count/state before deciding whether to retry. If the process is lost after commit, the next invocation resumes from the durable cursor. A duplicate invocation after terminal success is a no-op.

For an opted-in compressed hypertable, the executor transitions only through the contract's Timescale scheduler role, pauses the exact compression job, retains its original scheduled state in the checkpoint, and restores that state on success or bounded failure. A crash while paused is reconciled from the durable original state. The generated batch does not request tuple locks because TimescaleDB 2.16 rejects tuple locks on compressed rows; the same-transaction selected-versus-updated CAS fails closed on concurrent target changes.

## Abort and recovery

- Preflight rejection: fix the operational condition or create a newly reviewed/signed manifest. Never widen a budget in place without repeating review and signature promotion.
- Transactional failure: the DDL transaction rolls back; inspect the safe error code, repair the cause, and rerun.
- Online ordinary failure: confirm checkpoint manifest SHA/plan/state, compression-policy state, and target predicate; then rerun the identical signed package.
- Online lost process: do not edit the cursor. Reinvoke the identical SQL, manifest, public key, and target configuration. A mismatched manifest or plan is rejected.
- Failed postcondition or CAS mismatch: stop. Treat as concurrent data-shape drift and design a new plan; do not mark the migration succeeded manually.
- Rollback: already committed online batches are forward-only. Product rollback must be a separate reviewed migration with its own preimage, target predicate, impact manifest, and postcondition.

## Evidence checklist

Retain the canonical manifest/hash/signature verification result, SQL hash, public SPKI fingerprint, preflight metric summary, target/system identity, terminal checkpoint counts, postcondition result, policy before/after state, and schema migration terminal state. Do not record credentials, raw signing material, row payloads, or unbounded catalog dumps.
