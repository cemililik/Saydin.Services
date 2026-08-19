# Application rollback

Rollback is application-only unless an incident owner explicitly authorizes PITR. The
database migration chain is forward-only.

1. Identify the last known-good signed release whose declared schema range includes the
   current terminal migration/trust root. The rollback workflow accepts only the
   manifest named by the current manifest's `previousManifestSha256`; it does not permit
   arbitrary tags or skipping a release.
2. Preserve current logs, metrics, DQA and release evidence. Do not rebuild on-host or
   relax network, secret, role or limiter controls.
3. Replace only the API, ingestion and hardened Caddy digests in the stable
   `saydin-production` project. Keep control-plane, database, Redis, telemetry, backup
   images and all volumes in place; rerun current migrator verify-only and DQA before API.
4. Run API authority/finality, installation credential, quota/limiter and ingestion
   smoke tests. Confirm runtime DB backend census has no admin/superuser.
5. If no compatible digest exists, stop and forward-fix. Do not reverse SQL manually.
   For corruption/host loss follow the isolated PITR runbook and apply RPO/RTO approval.

Resolved when the previous signed digest is healthy for 30 minutes, alerts resolve and
the incident record contains the exact before/after digests.

Run `.github/workflows/rollback-production.yml` with an incident identifier and obtain
the protected `production-rollback` environment approval. A schema-range rejection has
no override: forward-fix or invoke the isolated restore incident procedure.
`rollback-release.sh` is not a lower-level bypass: it independently re-verifies the
current and previous keyless signatures, image attestations/SBOM digests, release tags,
source commits and adjacent manifest hash before invoking Docker Compose.
