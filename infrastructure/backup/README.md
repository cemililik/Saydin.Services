# Encrypted off-host PostgreSQL backup contract

The backup image uses a dedicated `LOGIN REPLICATION` role and never accepts the
bootstrap administrator. `pg_basebackup` creates a PostgreSQL SHA-256 manifest;
`pg_receivewal` continuously captures completed WAL segments. Restic encrypts all
content before it leaves the host and writes only to an `s3:https://...` repository.
The restic repository password and object-store workload-identity token are private
files materialized by the operator's KMS/identity agent.

Policy is fixed at RPO 15 minutes, RTO 120 minutes, WAL 14 days, weekly base backups
for 8 weeks, and monthly base backups for 12 months. `wal-stream` must run continuously;
`base-backup-loop` runs continuously and starts each base cycle every 24 hours. Base
backup uses `--wal-method=fetch`: the managed login's connection limit is two, so one
continuous WAL receiver and one base-backup session can coexist without the hidden
third session required by `--wal-method=stream`. The
deployment gate also runs one immediate `base-backup` before admitting the release.
Any missing dedicated role, private file, off-host repository, or exact policy value
fails closed. The frozen role contract provisions the versioned backup login, but
production admission installs an exact managed `hostssl` replication block followed
immediately by IPv4/IPv6 ordinary-SQL rejects. The release gate requires SQL deny and
an immediate verified base backup; CI proves a verified base while the bounded WAL
receiver is live under the exact two-connection limit. Do not
substitute `saydin_admin`.

WAL streaming uses the exact physical slot `<role-prefix>_backup_slot_v1`; the physical
protocol creates it idempotently. RoleBootstrap/Migrator verify the role and contract
through their separate control-plane identities. The backup login is deliberately
denied every ordinary SQL database connection, so the runtime never opens a SQL session
to inspect itself or the slot.
Production fixes `wal_keep_size=8GB` so fetch-mode base backup keeps a bounded WAL
window, and `max_slot_wal_keep_size=8GB` so an object-store outage cannot retain WAL
without bound. Exceeding either window can invalidate the recovery chain and is a
critical backup incident requiring an immediate new base and isolated drill.

Restore is permitted only at the exact `/restore-drill/work` leaf with the literal
confirmation `DISPOSABLE_RESTORE_ONLY`. The empty `/restore-drill` root and new leaf
must be process-owned, mode `0700` and free of symlinks; descriptor-relative no-follow
creation rejects traversal, broad, existing and nonprivate targets before Restic. It
restores into a new path and validates PostgreSQL's
`backup_manifest`; it never targets a production volume. Base and WAL snapshot
selection is restricted to the exact deployment host label. The base snapshot ID is
selected from Restic JSON as the newest snapshot at or before the requested UTC
instant; mutable table output and unsupported time-filter flags are not used.
