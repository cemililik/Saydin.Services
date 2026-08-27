# Production runbooks

These runbooks are defensive response procedures. Commands must target the release
manifest's exact deployment ID and signed digest; do not substitute the development
Compose file. Never print, copy, or pass secrets through environment variables or
argv. Database inspection uses the managed audit/control-plane identity appropriate
to the step—never an API/ingestion login and never an interactive superuser fallback.

Common order:

1. acknowledge the alert and record alert fingerprint, deployment ID, release digest,
   start time and incident owner;
2. establish whether the fault is external, runtime, data-plane or release-related;
3. preserve logs, metrics and DQA/backup evidence before changing state;
4. use a signed known-good digest or documented forward-fix; never rebuild on-host;
5. verify the alert resolves and attach measured recovery evidence.

Operator-only data correction uses [`data-repair.md`](data-repair.md). It binds the dormant
one-shot service to a signed release, requires an explicit dry-run/apply/rollback command and
preserves signed pending/final receipts in its dedicated durable volume.

Production DQA input, the exact 32-byte target authority and KMS-signed evidence retention
follow [`data-quality-audit.md`](data-quality-audit.md).

A database that has not yet crossed migration 018 must pass
[`scenario-integrity-migration.md`](scenario-integrity-migration.md). That gate is read-only by
default; over-cap scenario archival requires a separate encrypted export, exact-row admission
and independent deletion approval.

A non-empty database that has not crossed frozen migration 022 must pass
[`principal-retention-migration.md`](principal-retention-migration.md). The protocol requires a
production-shape rehearsal, independently approved storage/WAL headroom and timeouts, and a
writer-free maintenance window because 022 predates resumable impact admission.

Managed application-role credential rotation, reset and two-phase retirement follow
[`database-role-credential-lifecycle.md`](database-role-credential-lifecycle.md). Backup login
renewal remains a separate physical-replication procedure in
[`backup-login-renewal.md`](backup-login-renewal.md).

Targets: RPO 15 minutes, RTO 120 minutes. Production promotion remains blocked until
the backup/PITR drill and the alert routing game-day prove these objectives.
