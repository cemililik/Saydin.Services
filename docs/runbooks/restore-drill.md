# Isolated PITR restore drill

`.github/workflows/restore-drill.yml` runs at 02:17 UTC on the 1st and 15th of every
month and also supports manual dispatch. Keep the protected
`SAYDIN_RESTORE_SCHEDULE_RELEASE_TAG` variable bound to the deployed signed release.
Scheduled runs choose a target seven days behind the trusted restore-runner UTC clock: this remains
inside WAL retention and normally guarantees that a later transaction exists so
PostgreSQL can prove the configured target was reached. This is deliberately separate
from the current 15-minute RPO evidence. An entirely transaction-free seven-day period
can still fail closed; operators should rerun manually with a reachable historical
target rather than weakening the gate.
Run an additional drill after any PostgreSQL, Timescale, role-contract, migration,
backup-image, or object-store change. Manual runs supply a signed release tag and a UTC
second within the 14-day WAL window.

The DQA executable signs restore evidence with OCI KMS instance principal. The operator
environment supplies the canonical region, KMS key/version OCIDs, crypto endpoint,
allowed public-key fingerprints, and a matching public SPKI file; no private signing
key is mounted. The restored DB remains on an internal network. The one-shot encrypted
off-host backup fetch and DQA KMS signing steps alone receive a short-lived egress
network; neither the restored database nor API is attached to it.
The audit-output root is a non-symlink directory owned by UID/GID 1001 with mode `0700`;
each run-id/run-attempt leaf is created with the same exact ownership and mode.

Before approval, verify the restore runner has no production Docker context or production
volume mounts. Its contract file contains paths only. The per-consumer directories are
0700 and expose only their own 0400/0600 files. The bootstrap directory is control-plane
only and its `admin-connection` and `admin-pgpass` must target the disposable
`restored-db` alias; never
copy or rewrite a production connection file on the runner.

The backup restore primitive accepts only the exact `/restore-drill/work` leaf. Its
private `/restore-drill` root must be an empty, process-owned, non-symlink directory
with mode `0700`; the primitive creates `work/base` and `work/wal` with descriptor-
relative no-follow operations. Existing, broad, traversal, symlink or foreign-owner
targets fail before Restic runs.

The workflow performs these gates:

1. verify the release manifest, image signatures, SBOM hashes, previous-manifest chain,
   and keyless identity;
2. select the exact deployment's newest base snapshot no later than the target,
   restore that deployment's latest 14-day WAL set, and verify both Restic content and
   PostgreSQL's SHA-256 `backup_manifest`;
3. start PostgreSQL from a uniquely named disposable volume with recovery target action
   `promote` and no published port;
4. run RoleBootstrap verify, Migrator `--verify-only`, OCI KMS-signed DQA, and API health using
   separate managed credentials;
5. prove PostgreSQL reached the configured target by reaching ready state after
   recovery, and separately verify current RPO from the restored completed WAL segment
   source mtime plus its off-host observation/snapshot receipt;
6. write DQA evidence beneath the unique run-id/run-attempt leaf, sign a schema-v2
   receipt binding release-manifest digest, target, WAL evidence and execution identity,
   then remove and re-inspect every exact guarded container, network, and volume.

The last replayed transaction timestamp is informational because a quiet database may
have no recent commit. The hard current-RPO point is conservative: the later of the
completed segment source time and the observation time minus the 300-second segment
rotation budget must be no more than 900 seconds old when evaluated. Future, stale,
malformed, symlinked, or unconfirmed evidence fails closed. Any role, extension,
hypertable, trust-root, DQA, API, cleanup, or residual-resource gate failure invalidates
the drill. Preserve each unique DQA output leaf, signed receipt, logs, snapshot
identifiers, and incident link for 12 months, and monitor the audit-output filesystem.
