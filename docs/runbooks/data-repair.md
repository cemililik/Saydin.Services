# Production data repair

Use this procedure only for a signed, approved DataRepair plan during a recorded change
window. DataRepair is a dormant one-shot service: the normal deployment does not enable its
`data-repair-operator` profile, and its default command is the non-operational sentinel
`operator-command-required`. Never add it to `deploy-release.sh`, run it with `up`, or replace
its release-bound image with a local build.

## Admission and material

Record the incident/change ticket, operator, release id, release-manifest SHA-256, deployment
id, signed plan hash, evidence hash, requested mode and start time. Set absolute paths to the
checked-out repository, the keyless-verified release directory and the already rendered
production environment. Use the stable `saydin-production` Compose project.

```sh
REPO_ROOT=/absolute/path/to/Saydin.Services
RELEASE_DIR=/absolute/path/to/verified-release
ENV_FILE=/absolute/path/to/manifest-rendered-production.env
COMPOSE_FILE="$REPO_ROOT/infrastructure/deployment/compose.production.yml"
PROJECT=saydin-production

python3 "$REPO_ROOT/infrastructure/release/release_manifest.py" verify \
  --manifest "$RELEASE_DIR/release-manifest.json"
python3 "$REPO_ROOT/infrastructure/release/render-deployment-env.py" \
  --manifest "$RELEASE_DIR/release-manifest.json" --verify-existing "$ENV_FILE"
rendered=$(mktemp /tmp/saydin-data-repair.XXXXXX.json)
trap 'rm -f -- "$rendered"' EXIT HUP INT TERM
docker compose --project-name "$PROJECT" --env-file "$ENV_FILE" \
  --file "$COMPOSE_FILE" --profile '*' config --format json > "$rendered"
python3 "$REPO_ROOT/infrastructure/deployment/validate-production.py" "$rendered"
```

Verify that the service image is the `runtimeImages.data_repair` value and that this value is
derived from the signed first-party `data_repair` record. Also prove that the service is absent
from the default profile.

```sh
python3 - "$RELEASE_DIR/release-manifest.json" "$rendered" <<'PY'
import json, sys
manifest=json.load(open(sys.argv[1], encoding="utf-8"))
compose=json.load(open(sys.argv[2], encoding="utf-8"))
record=next(item for item in manifest["images"] if item["name"]=="data_repair")
expected=record["reference"]+"@"+record["digest"]
service=compose["services"]["data-repair"]
if (manifest["runtimeImages"]["data_repair"] != expected
        or service["image"] != expected
        or service.get("profiles") != ["data-repair-operator"]
        or service.get("command") != ["operator-command-required"]):
    raise SystemExit("data_repair_release_binding_rejected")
print("data_repair_release_binding_accepted")
PY
if docker compose --project-name "$PROJECT" --env-file "$ENV_FILE" \
  --file "$COMPOSE_FILE" config --services | grep -Fxq data-repair; then
  printf '%s\n' data_repair_default_profile_rejected >&2
  exit 78
fi
```

The root-only control plane must pre-create three external volumes. The secret volume's
`private/` directory is `0700`, uid 1001, and contains only `ingestion-current` and `audit-current` as
regular, single-link `0400`/`0600` files. The input volume root is `0700`, uid 1001, and holds
only the change-window material: `plan.json`, `plan.sig`, `plan-public.pem`, `evidence/`,
`evidence-public.pem`, `approval-token`, and `receipt-public.pem`. Keep every private input and
its parent owner-private; do not use symlinks. Dry-run does not need `approval-token`, but apply
and rollback do.

The receipt volume is durable state, not scratch space. Before every run, fail closed unless its
root is an existing external volume owned by uid 1001 with mode `0700`. This preflight is
read-only and does not repair permissions.

```sh
helper_image=$(python3 - "$rendered" <<'PY'
import json, sys
print(json.load(open(sys.argv[1], encoding="utf-8"))["services"]["postgres"]["image"])
PY
)
receipt_volume=$(python3 - "$rendered" <<'PY'
import json, sys
print(json.load(open(sys.argv[1], encoding="utf-8"))["volumes"]["data_repair_receipts"]["name"])
PY
)
input_volume=$(python3 - "$rendered" <<'PY'
import json, sys
print(json.load(open(sys.argv[1], encoding="utf-8"))["volumes"]["data_repair_input"]["name"])
PY
)
secret_volume=$(python3 - "$rendered" <<'PY'
import json, sys
print(json.load(open(sys.argv[1], encoding="utf-8"))["volumes"]["data_repair_secret"]["name"])
PY
)
for volume in "$receipt_volume" "$input_volume" "$secret_volume"; do
  docker volume inspect "$volume" >/dev/null
done
for volume in "$receipt_volume" "$input_volume"; do
  docker run --rm --network none --read-only --cap-drop ALL \
    --security-opt no-new-privileges:true --user 0:0 --pids-limit 64 \
    --memory 128m --cpus 0.25 --tmpfs /tmp:mode=0700,size=8m \
    --mount "type=bind,src=$REPO_ROOT/infrastructure/deployment/validate-runtime-volume.py,dst=/validator.py,readonly" \
    --mount "type=volume,src=$volume,dst=/material,readonly" \
    --entrypoint python3 "$helper_image" /validator.py --uid 1001 /material
done
docker run --rm --network none --read-only --cap-drop ALL \
  --security-opt no-new-privileges:true --user 0:0 --pids-limit 64 \
  --memory 128m --cpus 0.25 --tmpfs /tmp:mode=0700,size=8m \
  --mount "type=bind,src=$REPO_ROOT/infrastructure/deployment/validate-private-material.py,dst=/validator.py,readonly" \
  --mount "type=volume,src=$secret_volume,dst=/material,readonly" \
  --entrypoint python3 "$helper_image" /validator.py data-repair /material/private
```

## Dry-run, apply and rollback

Read the nonsecret login and KMS identifiers from the validated service model. Do not copy
passwords into the environment or command line.

```sh
repair_config_value() {
  python3 - "$rendered" "$1" <<'PY'
import json, sys
env=json.load(open(sys.argv[1], encoding="utf-8"))["services"]["data-repair"]["environment"]
value=env[sys.argv[2]]
if not isinstance(value, str) or not value or "\n" in value or "\r" in value:
    raise SystemExit("data_repair_config_value_rejected")
print(value)
PY
}
AUDIT_LOGIN=$(repair_config_value SAYDIN_DATA_REPAIR_AUDIT_LOGIN)
KMS_KEY_ID=$(repair_config_value SAYDIN_DATA_REPAIR_KMS_KEY_ID)
KMS_KEY_VERSION_ID=$(repair_config_value SAYDIN_DATA_REPAIR_KMS_KEY_VERSION_ID)
KMS_ENDPOINT=$(repair_config_value SAYDIN_DATA_REPAIR_KMS_CRYPTO_ENDPOINT)
OCI_REGION=$(repair_config_value SAYDIN_DATA_REPAIR_OCI_REGION)
compose() {
  docker compose --project-name "$PROJECT" --env-file "$ENV_FILE" \
    --file "$COMPOSE_FILE" --profile data-repair-operator "$@"
}
```

First run the exact signed plan as dry-run. It takes the live target/trust lease and evaluates
all preconditions but writes neither database state nor a receipt.

```sh
compose run --rm --no-deps data-repair dry-run \
  --plan /run/repair/plan.json \
  --plan-signature /run/repair/plan.sig \
  --plan-public-key /run/repair/plan-public.pem \
  --evidence-bundle /run/repair/evidence \
  --evidence-public-key /run/repair/evidence-public.pem \
  --audit-login "$AUDIT_LOGIN" \
  --audit-password-file /run/saydin-secrets/private/audit-current
```

After an independent reviewer accepts the dry-run evidence and the signed approval-token hash,
replace `MODE` with exactly `apply` or `rollback`. This explicit command is the only production
path to a destructive mode.

```sh
MODE=apply
case "$MODE" in apply|rollback) ;; *) exit 64 ;; esac
compose run --rm --no-deps data-repair "$MODE" \
  --plan /run/repair/plan.json \
  --plan-signature /run/repair/plan.sig \
  --plan-public-key /run/repair/plan-public.pem \
  --evidence-bundle /run/repair/evidence \
  --evidence-public-key /run/repair/evidence-public.pem \
  --audit-login "$AUDIT_LOGIN" \
  --audit-password-file /run/saydin-secrets/private/audit-current \
  --approval-token-file /run/repair/approval-token \
  --receipt-root /var/lib/saydin/repair-receipts \
  --receipt-signer-mode oci-kms-instance-principal \
  --kms-key-id "$KMS_KEY_ID" \
  --kms-key-version-id "$KMS_KEY_VERSION_ID" \
  --kms-crypto-endpoint "$KMS_ENDPOINT" \
  --oci-region "$OCI_REGION" \
  --receipt-public-key /run/repair/receipt-public.pem \
  --kms-timeout-seconds 10
```

Never change a plan, approval token, receipt, or pending directory to make a retry pass. Rerun the
same signed bytes and nonce after an uncertain acknowledgement or
`receipt_publish_after_commit_failed`; DataRepair reconciles the database pre/postimage before
promoting a pending receipt. Preserve pending receipts because they are recovery state. Preserve
final receipt directories, their detached signatures and public-key identity for the financial
retention period, and copy the entire receipt volume to encrypted off-host immutable storage.
Record the final receipt hash, KMS key/version, database transaction id and operator command in
the change record. Do not delete or reuse the persistent receipt volume during deployment,
rollback, input cleanup or incident closure.

Real OCI instance-principal/KMS signing and a production database repair are external acceptance
steps; repository validation cannot simulate either authority.
