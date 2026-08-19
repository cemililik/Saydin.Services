# Release supply-chain incident

Every first-party API, ingestion, database control, calendar, DQA, backup, and Caddy
image is built once for `linux/amd64` and `linux/arm64`. The release workflow publishes
the multi-architecture digest, SPDX and CycloneDX SBOMs, BuildKit/GitHub provenance,
Trivy vulnerability/license result, keyless image signature, and a canonical signed
release manifest. Deployment never resolves a mutable tag.

The signing workflow must execute from `refs/heads/main`, and a new release tag must
resolve to that exact dispatch commit. Each of the seven image records independently
binds the same source commit. Deployment controllers remain checked out at their
current trusted main commit; a release tag is resolved read-only and never supplies the
signature-verification or deployment scripts.

If signature, certificate identity, SBOM hash, provenance, digest, manifest canonicality,
or previous-manifest linkage fails:

1. stop promotion; do not re-sign, waive, or edit the manifest;
2. preserve the workflow run, OIDC certificate, Rekor bundle, digest, scanner database
   version, and release assets;
3. revoke package access if compromise is plausible and notify the configured on-call;
4. rebuild from a reviewed new commit and new tag after fixing the cause; never overwrite
   a released tag or digest;
5. if production is affected, use only a signed schema-compatible adjacent rollback.

The environment protection rule supplies human approval. Registry is GHCR and signing is
GitHub OIDC keyless Cosign. Domain, object-store bucket/KMS identity, and on-call routes
remain operator-owned inputs and must be resolved before first deployment.
