# Encrypted off-host PostgreSQL backup contract

The backup image uses a dedicated `LOGIN REPLICATION` role and never accepts the
bootstrap administrator. `pg_basebackup` creates a PostgreSQL SHA-256 manifest;
`pg_receivewal` continuously captures completed WAL segments. Restic encrypts all
content before it leaves the host and writes only to an `s3:https://...` repository.
The restic repository password and object-store workload-identity token are private
files materialized by the operator's KMS/identity agent.

Policy is fixed at RPO 15 minutes, RTO 120 minutes, WAL 14 days, weekly base backups
for 8 weeks, and monthly base backups for 12 months. `wal-stream` must run continuously;
`base-backup-loop` runs continuously and takes a base immediately when no valid base
success exists within the preceding 24 hours. On restart it validates the exact private
success-metric file and sleeps only the remaining interval, avoiding a duplicate base
after the deployment's verified one-shot. Transient transfer/repository failures use a
60-second exponential backoff capped at 15 minutes without terminating the scheduler;
password/HBA/replication-role/SSL configuration, local target, target-identity and
manifest failures remain fail-closed. Base
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

Plain-format base backups are staged only on the external
`SAYDIN_BACKUP_BASE_STAGING_VOLUME`, mounted exactly at
`/var/lib/saydin-backup/base-staging`. The mount must be a real mount point owned by
`1001:1001` with mode `0700` on a non-`tmpfs`/non-`ramfs` filesystem; the configured
free-space floor must be at least 8 GiB and should be sized above the largest expected
plain PGDATA plus operational headroom.
The runtime serializes the scheduler and deployment's immediate base job with a local
bounded lock, rejects symlink/owner/mode/capacity drift, keeps the Restic cache on this
disk, and removes only the guarded `current` child on success, error, HUP, INT or TERM.
SIGKILL cannot run cleanup; the next lock owner validates and removes a correctly owned
stale `current` directory before starting.

Provision the external volume before deployment and initialize only that exact volume:

```sh
docker volume create "$SAYDIN_BACKUP_BASE_STAGING_VOLUME"
docker run --rm --user 0:0 --read-only --cap-drop ALL --cap-add CHOWN \
  --security-opt no-new-privileges --network none \
  -v "$SAYDIN_BACKUP_BASE_STAGING_VOLUME:/var/lib/saydin-backup/base-staging" \
  --entrypoint /bin/sh "$SAYDIN_BACKUP_IMAGE" \
  -c 'chmod 0700 /var/lib/saydin-backup/base-staging && chown 1001:1001 /var/lib/saydin-backup/base-staging'
```

Do not reuse the PostgreSQL data, WAL spool, metrics, secret, or restore volumes for
base staging. Set `SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES` to the operator-approved
capacity floor; values below 8589934592 are rejected.

Both backup lanes require the metrics mount to be a real, process-owned, non-group/world
writable directory. Startup performs a create/remove write probe before any backup. The
base scheduler additionally accepts only a single-link, process-owned, mode-`0600`
success metric with the exact expected line format and a non-future timestamp; missing
or stale success means an immediate base, while malformed state fails closed.

WAL streaming uses the exact physical slot `<role-prefix>_backup_slot_v1`; the physical
protocol creates it idempotently. RoleBootstrap/Migrator verify the role and contract
through their separate control-plane identities. The backup login is deliberately
denied every ordinary SQL database connection, so the runtime never opens a SQL session
to inspect itself or the slot.
Production fixes `wal_keep_size=8GB` so fetch-mode base backup keeps a bounded WAL
window, and `max_slot_wal_keep_size=8GB` so an object-store outage cannot retain WAL
without bound. Exceeding either window can invalidate the recovery chain and is a
critical backup incident requiring an immediate new base and isolated drill.
PostgreSQL is started with `archive_timeout=300s`, bounding completed-segment rotation
after important WAL activity during low traffic, and the uploader scans every 300
seconds. A completely idle database produces no new recovery state and need not force
an empty segment. The healthy-path budget is therefore at most 5 minutes to complete a
segment after important WAL activity, 5 minutes to discover it, and 5 minutes of
remaining transfer/lock headroom against the 15-minute RPO. Object-store
latency, a contended lock, or an outage can consume that headroom; the completion-time
metric remains old and the RPO alert must fire rather than claiming compliance. The WAL
lane writes an observation marker and encrypted off-host snapshot on every healthy
300-second loop, including quiet periods. The marker binds a completed 24-hex segment,
its source mtime, and the observation time to that same snapshot. Before publishing the
marker, the same physical credential obtains `IDENTIFY_SYSTEM` and `wal_segment_size`;
the system-id must match and the local completed segment must be the server's current
or immediately previous segment. Base transfer holds the shared physical-probe lock so
the managed login's two-connection limit is never exceeded. While that lock is held,
the WAL lane still encrypts and uploads completed segments in a `wal-unverified`
snapshot, but excludes the observation marker and does not advance the recovery-point
metric or verified watermark. The next successful probe publishes a normal
`wal,wal-observation` snapshot containing the retained spool. A lagging receiver or
timeline mismatch follows the same unverified path and produces no freshness claim.
Only after a high-water-bound snapshot succeeds does the lane atomically advance the
durable segment watermark when the segment is newer and retain the completed segment
mtime in `saydin_backup_wal_last_segment_timestamp_seconds`. Every successful high-water-bound
snapshot updates the compatibility freshness metric to the conservative later of the
source mtime and observation time minus the 300-second rotation budget. This prevents a
backlog from clearing the RPO alert while avoiding quiet-traffic false alarms;
`.partial` files never count as completed segments. Repository operations wait at most
15 minutes for a Restic lock. Snapshot
retention runs without prune on the hot base/WAL paths, while repository prune is
scheduled independently at most once per seven days after a successful base cycle.

The WAL spool is an exact external, non-memory mount owned by `1001:1001` with mode
`0700`. Runtime admission requires
`SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES >= 103079215104` (96 GiB), checks the floor at
startup and every upload interval, and publishes
`saydin_backup_wal_spool_free_bytes` plus
`saydin_backup_wal_spool_capacity_floor_bytes`. The estimate is
`86400 / archive_timeout_seconds * wal_segment_size * retention_days`: with the fixed
300-second rotation bound, 16 MiB segments and 14 days this is about 63 GiB before
Restic cache and filesystem headroom. Missing/low capacity metrics page and the process
fails closed below the configured floor. Fixed same-filesystem `.tmp` marker names are
removed on startup/normal exit and excluded from every snapshot, so an interrupted
atomic publication cannot poison restore inventory.

The Docker acceptance smoke uses the pinned production PostgreSQL image with accelerated
`archive_timeout=30s` and `checkpoint_timeout=30s`. It waits for the final postmaster
(not the image's temporary init server), starts a real synchronous `pg_receivewal`,
commits permanent WAL, and requires the same 24-hex segment to change from `.partial`
to an exact 16 MiB completed file without `archive_mode`. This locks the runtime
behavior behind the production 300-second setting while keeping the CI gate bounded.

Restore is permitted only at the exact `/restore-drill/work` leaf with the literal
confirmation `DISPOSABLE_RESTORE_ONLY`. The empty `/restore-drill` root and new leaf
must be process-owned, mode `0700` and free of symlinks; descriptor-relative no-follow
creation rejects traversal, broad, existing and nonprivate targets before Restic. It
restores into a new path and validates PostgreSQL's
`backup_manifest`; it never targets a production volume. Base and WAL snapshot
selection is restricted to the exact deployment host label. The base snapshot ID is
selected from Restic JSON as the newest snapshot at or before the requested UTC
instant; mutable table output and unsupported time-filter flags are not used.
