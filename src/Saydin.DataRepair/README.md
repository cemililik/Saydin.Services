# Saydin Data Repair runbook

`Saydin.DataRepair` is a fail-closed executor for signed, pre-approved repair plans. It does not
accept SQL, table names, predicates, connection strings, or passwords in a plan or on the command
line. The only mutating operation in the current plan schema version 2 is
`requeue_permanent_window`. For a calendar-bound permanent failure it atomically returns the
window to `pending` and clears the stale release binding; the next normal claim must bind the
current active sealed release. Schema-v1 plans remain executable with their original
retryable-failed/no-rebind semantics.
`refetch` and `manual_review` produce bounded receipt work-order entries and do not mutate the
database.

## Trust inputs

Prepare every input below before opening a change window:

- A canonical JSON plan no larger than 64 KiB, a detached P-256 DER signature, and the signer
  public SPKI key. All three files must pass `SecureSecretFile`: absolute regular path, private
  owner, no links, private parent directory, and bounded size.
- An immutable DQA evidence directory containing the signed schema-v2 manifest, manifest hash,
  detached signature, and exact declared file inventory. The plan binds both
  `evidence.contentSha256` and `evidence.signerKeyId`. Production accepts only an
  `oci-kms-instance-principal` DQA signer; development and staging may use `local-pem`. The
  production decision is made only after the signed database/system/deployment/role target has
  been verified against the live physical database.
- The exact database name, PostgreSQL system-identifier SHA-256, deployment id, derived role
  prefix, the complete embedded migration versions/checksums and manifest hash, issued/expiry times,
  change ticket, nonce, receipt key id, and approval-token SHA-256 in the signed plan.
- Separate exact current managed credentials for the configured ingestion and audit login
  versions. Passwords are private files. The audit credential is used only for
  live trust verification and is never passed to the mutation repository.

The executor takes the same physical-target advisory lock as the role bootstrap and migrator.
While holding it, the audit session verifies the database/system identity, exact role contract,
`saydin_migration_control=ready`, the exact embedded migration set/checksums, and its read-only ACL.
The ingestion session is independently identity-checked and must be able to update the ingestion
ledger while remaining unable to read `schema_migrations`.

## Runtime environment

Build the rootless, digest-pinned one-shot image from repository root:

```sh
docker build -f src/Saydin.DataRepair/Dockerfile -t saydin-data-repair:local .
```

The image runs as uid `1001`. Mount trust inputs and password files read-only; mount a dedicated,
durable `0700`, uid-1001-owned volume at `/var/lib/saydin/repair-receipts`. Never reuse an API,
ingestion, database, or backup writable volume. The production release builds and attests this
image as `data_repair`; the manifest derives `runtimeImages.data_repair` from that signed record,
and Compose consumes only `SAYDIN_DATA_REPAIR_IMAGE`. Production operation follows the canonical
[`../../docs/runbooks/data-repair.md`](../../docs/runbooks/data-repair.md) operator-only procedure.

Set only topology and password-file references; raw credential environment variables are rejected:

```text
SAYDIN_ENVIRONMENT=development|staging|production
PGHOST=<exact host>
PGPORT=5432
PGDATABASE=<plan target database>
PGUSER=<prefix>_ingestion_login_vN
PGSSLMODE=disable|require|verify-ca|verify-full
SAYDIN_DEPLOYMENT_ID=<plan deployment id>
SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256=<64 lowercase hex>
SAYDIN_DATABASE_ROLE_PREFIX=<plan role prefix>
SAYDIN_DATABASE_LOGIN_VERSION=N
SAYDIN_INGESTION_DATABASE_PASSWORD_FILE=/run/secrets/ingestion-current
```

## Dry-run and approval

Dry-run is the default when no verb is supplied. It still acquires the target lock, performs live
audit verification, and validates every preimage and safety guard, but commits no database write
and emits no receipt.

```sh
dotnet Saydin.DataRepair.dll dry-run \
  --plan /run/repair/plan.json \
  --plan-signature /run/repair/plan.sig \
  --plan-public-key /run/repair/plan-public.pem \
  --evidence-bundle /run/repair/evidence \
  --evidence-public-key /run/repair/evidence-public.pem \
  --audit-login '<prefix>_audit_login_vN' \
  --audit-password-file /run/secrets/audit-current
```

An apply or rollback additionally requires a private approval-token file whose SHA-256 is signed
into the plan, a pre-existing private `0700` receipt root, and a receipt signer. In production the
only permitted signer is OCI KMS instance principal; local private-key arguments and private-key
environment variables are rejected.

```sh
dotnet Saydin.DataRepair.dll apply \
  <the common arguments above> \
  --approval-token-file /run/repair/approval-token \
  --receipt-root /var/lib/saydin/repair-receipts \
  --receipt-signer-mode oci-kms-instance-principal \
  --kms-key-id '<key OCID>' \
  --kms-key-version-id '<key-version OCID>' \
  --kms-crypto-endpoint 'https://<vault>-crypto.kms.<region>.oraclecloud.com/' \
  --oci-region '<region>' \
  --receipt-public-key /run/repair/receipt-public.pem
```

Use the same plan, approval token, receipt root, and receipt signer with the `rollback` verb.
Rollback succeeds only when the apply receipt is valid and the exact postimage plus related
job/data/attribution guard is unchanged. A repeated apply or rollback is idempotent only for the
same signed plan and nonce; nonce reuse with different plan bytes is rejected. A final apply
receipt also remains idempotent after the normal ingestion writer has claimed or completed the
requeued window, provided a post-receipt correlated ingestion job proves that progression;
rollback remains fail-closed once normal ingestion has advanced the postimage.

## Receipt and failure handling

Before commit the executor writes a signed private pending receipt containing hashes, operation
indexes, transaction id, and rollback state—never passwords, paths, raw business keys, request
bodies, or SQL. After a confirmed commit it atomically renames the pending directory to final and
`fsync`s the receipt-root directory so the rename is durable across host failure.
After an uncertain commit acknowledgement it compares the database with the signed pre/postimage:

- exact postimage: promote the pending receipt and report `reconciled`;
- exact preimage: delete the pending receipt and report not applied;
- neither: stop with `repair_commit_state_uncertain`; do not retry or edit the receipt manually.

No final receipt is produced for signature, target, precondition, CAS, or transaction failures.
Preserve a pending receipt after `receipt_publish_after_commit_failed`; rerun the exact same signed
plan to reconcile it.

## Required acceptance

From repository root, the disposable test harness builds the pinned migrator image, creates a
UUID-bound TimescaleDB database, runs pre-bootstrap, the complete migration trust root,
backup-HBA/post-bootstrap, migrator verify, and the exact managed ingestion/audit suite. It removes its container, network,
volumes, and one-off image on every exit.

```sh
bash tests/Saydin.DataRepair.IntegrationTests/run-isolated.sh
```

The runner rejects `TEST_FILTER`, requires at least 33 executed tests from its TRX, and requires
zero failed and zero skipped tests. Never point the harness or the executor at a live
production database during testing.
