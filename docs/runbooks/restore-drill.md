# Isolated PITR restore drill

Run `.github/workflows/restore-drill.yml` at least monthly and after any PostgreSQL,
Timescale, role-contract, migration, backup-image, or object-store change. Supply a
signed release tag and a UTC second within the 14-day WAL window.

The DQA executable signs restore evidence with OCI KMS instance principal. The operator
environment supplies the canonical region, KMS key/version OCIDs, crypto endpoint,
allowed public-key fingerprints, and a matching public SPKI file; no private signing
key is mounted. The restored DB remains on an internal network. The one-shot encrypted
off-host backup fetch and DQA KMS signing steps alone receive a short-lived egress
network; neither the restored database nor API is attached to it.

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
5. sign a receipt binding release-manifest digest and target time, then remove the exact
   guarded containers, network, and volume.

Success requires completion within 120 minutes and a recovered point no more than 15
minutes behind the requested target. Any role, extension, hypertable, trust-root, DQA,
or API gate failure invalidates the drill. Preserve the signed receipt, logs, backup
snapshot identifiers, and incident link for 12 months.
