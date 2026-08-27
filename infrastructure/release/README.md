# Immutable release and promotion control plane

The release workflow builds API, ingestion, database control, calendar, DataRepair, DQA,
backup, and the hardened Caddy derivative once. Each OCI index contains only `linux/amd64` and
`linux/arm64`; the workflow signs the index and both platform manifests, creates and
attests per-platform SPDX/CycloneDX SBOMs, scans each platform for High/Critical
vulnerabilities and license findings, and publishes GitHub/BuildKit provenance.

`release-manifest.json` is canonical JSON. It binds source commit/workflow, terminal
migration and Migrator trust-root hash, compatible schema range, the immediately
previous manifest hash, all first-party index/platform digests and SBOM hashes, and
every third-party production image digest. The exact 12-key `runtimeImages` authority contains
11 reviewed external entries plus `data_repair`, which is derived from and must equal the signed
first-party `data_repair` record's `reference@digest`. The external runtime-image lock is an
exact 11-key nonsecret absolute file on the release runner; use `runtime-images.lock.example.json`
as its shape. Placeholder, tag, duplicate/unknown field, missing platform, policy
drift, and a non-adjacent rollback are rejected.

Required GitHub repository/environment configuration:

- `SAYDIN_RUNTIME_IMAGE_LOCK_FILE`: reviewed absolute runtime-image lock on the release
  runner;
- staging/production operator environment and authenticated curl-config paths;
- restore operator/contract paths, each scoped to the restore runner;
- protected `staging`, `production`, `production-rollback`, and `restore-drill`
  environments with the labelled self-hosted runners;
- GHCR package access and GitHub OIDC keyless signing; no stored Cosign private key.

Deployment environment files contain identifiers, volume names, public host and
resource limits only. `render-deployment-env.py` replaces every deployable image with
the signed manifest value. Consumer credentials remain in externally materialized
private volumes; no workflow writes them to `GITHUB_ENV`, argv, a release asset, or a
container environment.

Production inputs are checked before runtime mutation: OCI KMS identity/key metadata,
versioned backup login validity, dedicated backup CIDR, TLS material, and private-volume
shapes must all pass. Migration 022 and its trust root are frozen. RoleBootstrap and
Migrator verify the backup login's exact NOINHERIT/REPLICATION-only attributes and lack
of memberships/capabilities; the backup runtime accepts only that exact versioned
username and then uses physical protocol because ordinary SQL is rejected by HBA. Do
not set an administrator as a workaround.

Promotion uses the stable `saydin-production` Compose project so it updates the
admitted stack instead of competing for ports and external volumes. Rollback verifies
the currently running API/Caddy digests, current migration trust and DQA first, then
changes only the signed previous API, ingestion and Caddy images. Control-plane,
database, telemetry and backup images remain on the admitted current release. A failed
rollback smoke attempts an immediate return to the current digests and never reverses
a migration. The rollback executable repeats keyless signature, SBOM, provenance,
adjacent-manifest hash and schema-compatibility admission for both current and target
release directories before its first Compose/Docker mutation; workflow composition is
not a substitute for this primitive-level check.

Local, non-publishing validation:

```sh
python3 infrastructure/release/tests/release-manifest-self-test.py
python3 infrastructure/release/tests/rollback-admission-self-test.py
python3 infrastructure/backup/tests/backup-static-self-test.py
python3 infrastructure/backup/tests/backup-hba-self-test.py
python3 infrastructure/release/validate-release.py
infrastructure/deployment/validate-production.sh \
  "$PWD/infrastructure/deployment/tests/production.validation.env"
```

Use Actionlint against all five workflow files and build the DataRepair/DQA/backup Dockerfiles on
both target platforms before enabling release. Local validation never signs, pushes,
deploys, initializes a repository, or touches a production Docker context.

Production repair is not part of deployment or rollback service startup. Follow the
operator-only [`../../docs/runbooks/data-repair.md`](../../docs/runbooks/data-repair.md) procedure;
it verifies the same signed manifest binding before an explicit one-shot command.
