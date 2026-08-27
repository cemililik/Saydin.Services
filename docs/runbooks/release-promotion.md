# Release promotion

Promotion moves an already-built digest set from staging approval to production; it
never rebuilds or trusts a mutable tag.

Automation entry points are `release-images.yml`, `deploy-staging.yml`, and
`promote-production.yml`. They run only on labelled self-hosted environment runners.
Configure GHCR access and GitHub OIDC keyless signing in GitHub; configure the public
domain, object-store/KMS workload identity, on-call route, private volume materializer,
and absolute operator environment/smoke files in environment variables. Never store
credential values in Actions variables or `GITHUB_ENV`.

1. Verify the release record binds commit, API/ingestion/control/calendar/DataRepair/DQA/
   backup/Caddy manifest-list digests, the 11 external runtime digests, the derived exact
   `runtimeImages.data_repair` binding, SBOMs, provenance and signatures.
2. Require migrations through 022 and every later migration in the release to be frozen in the
   Migrator and DQA trust roots. Unknown tail or checksum drift stops promotion.
3. Deploy the exact digest set to staging. Run role bootstrap, migrator, DQA, API trust,
   quota/limiter, ingestion and backup/alert smoke gates.
4. Record human production-environment approval. Re-resolve every digest and prove it
   is byte-identical to staging before touching production.
5. Follow the ordered deployment in `infrastructure/deployment/README.md`; admit Caddy
   traffic only after post-deploy smoke and backend least-privilege census pass.

Staging and production use stable `saydin-staging` and `saydin-production` Compose
projects; run IDs belong in signed receipts, not project names. This makes promotion an
in-place digest update and prevents a second stack from competing for external volumes
or ports 80/443. Production admission and adjacent rollback receipts are keyless-signed
and retained as immutable release assets.

`deploy-release.sh` deliberately stops before admission when OCI KMS metadata/public
key allowlisting, dedicated backup TLS/HBA, phase-aware post-bootstrap, physical backup,
or DQA signing fails. Migration 022 is frozen at SHA-256
`568017c27eb6038a06b48ee00f2f0820bba6cf7b577dd5f283291ac9995e8afd`; the terminal
migration count is 24.

The release evidence must retain approval, migration result, DQA evidence, restore-drill
age, alert game-day and final running digests.
