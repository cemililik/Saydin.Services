# Production data-quality audit

Run DQA only from the digest-pinned `data-quality-audit` service after Migrator verify-only
passes. The signed schema-v2 input manifest, its detached signature and public key belong in the
owner-private `audit_input` volume. The evidence output must be an empty, durable owner-private
volume; archive the complete signed bundle after the run.

## Production-target authority

Production manifests require an exact 32-byte target-authority file. It is a deployment target
declaration, not a reusable credential: it binds the manifest's database name and PostgreSQL
system-identifier SHA-256 to `saydin-dqa-production-target/v1`. A missing, stale or wrongly
shaped file fails closed before KMS client creation.

Generate it on the root-only materialization host from the independently verified signed
manifest. Do not copy a value from another database or encode the digest as hexadecimal text.

```sh
MANIFEST=/absolute/path/to/audit-input/manifest.json
TARGET_FILE=/secure/staging/production-target
python3 - "$MANIFEST" "$TARGET_FILE" <<'PY'
import hashlib, json, os, pathlib, sys

manifest = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
target = manifest["target"]
database = target["database"]
identity = target["systemIdentifierSha256"]
if (not isinstance(database, str) or not database or "\x00" in database
        or not isinstance(identity, str) or len(identity) != 64
        or any(ch not in "0123456789abcdef" for ch in identity)):
    raise SystemExit("production_target_input_rejected")
payload = f"saydin-dqa-production-target/v1\0{database}\0{identity}".encode("utf-8")
path = pathlib.Path(sys.argv[2])
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o400)
with os.fdopen(fd, "wb") as stream:
    stream.write(hashlib.sha256(payload).digest())
PY
test "$(wc -c < "$TARGET_FILE" | tr -d ' ')" = 32
chown 1001:1001 "$TARGET_FILE"
chmod 0400 "$TARGET_FILE"
```

Install it as `private/production-target` in the pre-created `audit_secret` volume. That
directory must contain exactly `password`, `evidence-hmac`, `evidence-public.pem`, and
`production-target`; all are regular, single-link files owned by uid 1001 with mode `0400` or
`0600`. Validate the read-only volume before DQA:

```sh
docker run --rm --network none --read-only --cap-drop ALL \
  --security-opt no-new-privileges:true --user 0:0 \
  --mount type=bind,src=/absolute/path/to/validate-private-material.py,dst=/validator.py,readonly \
  --mount type=volume,src="$SAYDIN_AUDIT_SECRET_VOLUME",dst=/material,readonly \
  --entrypoint python3 "$SAYDIN_POSTGRES_IMAGE" /validator.py audit /material/private
```

## Execute and retain evidence

Render and validate the production Compose model, then run only the audit profile. The checked-in
command supplies `--production-target-authority-file
/run/saydin-secrets/private/production-target` and uses OCI instance principal with the
allowlisted KMS key/version; a production private PEM is forbidden.

```sh
docker compose --project-name saydin-production --env-file /absolute/path/production.env \
  --file infrastructure/deployment/compose.production.yml --profile audit \
  run --rm --no-deps data-quality-audit
```

Require exit zero, verify the emitted bundle with its public key, and record the deployment id,
manifest hash, evidence hash, KMS key/version and output location in the change record. Never
overwrite earlier evidence. A target-authority mismatch means the signed input or deployment
identity is wrong; regenerate only after independently resolving that discrepancy, never to
force a run through.
