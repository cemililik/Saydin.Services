#!/bin/sh
set -eu
umask 077

die() { printf '%s\n' "$1" >&2; exit "${2:-70}"; }
[ "$#" -eq 7 ] || die "deployment_usage" 64
environment=$1
project=$2
compose_file=$3
env_file=$4
release_dir=$5
receipt_dir=$6
workflow_run_id=$7

case "$environment" in staging|production) ;; *) die "deployment_environment_invalid" 64 ;; esac
[ "$project" = "saydin-$environment" ] || die "deployment_project_guard_failed" 64
case "$workflow_run_id" in ""|*[!0-9]*) die "deployment_workflow_run_id_invalid" 64 ;; esac
for path in "$compose_file" "$env_file" "$release_dir/release-manifest.json"; do
  case "$path" in /*) ;; *) die "deployment_absolute_path_required" 64 ;; esac
  [ -f "$path" ] || die "deployment_input_missing" 66
done
case "$receipt_dir" in /*) ;; *) die "deployment_absolute_path_required" 64 ;; esac
[ ! -e "$receipt_dir" ] || die "deployment_receipt_target_exists" 73
mkdir -m 0700 "$receipt_dir"

manifest_sha=$(python3 infrastructure/release/release_manifest.py verify --manifest "$release_dir/release-manifest.json")
if ! python3 - "$release_dir/release-manifest.json" "$env_file" <<'PY'
import json, sys
manifest=json.load(open(sys.argv[1],encoding="utf-8")); values={}
for number,line in enumerate(open(sys.argv[2],encoding="utf-8"),1):
    line=line.rstrip("\n")
    if not line or line.startswith("#"): continue
    key,sep,value=line.partition("=")
    if not sep or key in values: raise SystemExit("deployment_env_invalid")
    values[key]=value
first={"api":"SAYDIN_API_IMAGE","ingestion":"SAYDIN_INGESTION_IMAGE","control":"SAYDIN_CONTROL_IMAGE",
       "calendar":"SAYDIN_CALENDAR_IMAGE","dqa":"SAYDIN_DQA_IMAGE","backup":"SAYDIN_BACKUP_IMAGE","caddy":"SAYDIN_CADDY_IMAGE"}
runtime={"timescale":"SAYDIN_TIMESCALE_IMAGE","redis":"SAYDIN_REDIS_IMAGE",
         "postgresExporter":"SAYDIN_POSTGRES_EXPORTER_IMAGE","redisExporter":"SAYDIN_REDIS_EXPORTER_IMAGE",
         "otel":"SAYDIN_OTEL_IMAGE","prometheus":"SAYDIN_PROMETHEUS_IMAGE","alertmanager":"SAYDIN_ALERTMANAGER_IMAGE",
         "blackbox":"SAYDIN_BLACKBOX_IMAGE","nodeExporter":"SAYDIN_NODE_EXPORTER_IMAGE"}
expected={first[item["name"]]:item["reference"]+"@"+item["digest"] for item in manifest["images"]}
expected.update({runtime[name]:reference for name,reference in manifest["runtimeImages"].items()})
if any(values.get(key)!=value for key,value in expected.items()): raise SystemExit("deployment_manifest_image_mismatch")
if (values.get("SAYDIN_GIT_SHA")!=manifest["source"]["commitSha"]
        or values.get("SAYDIN_RELEASE_VERSION")!=manifest["releaseId"]
        or values.get("SAYDIN_SERVICE_VERSION")!=manifest["releaseId"]):
    raise SystemExit("deployment_manifest_source_mismatch")
PY
then
  die "deployment_manifest_binding_failed" 78
fi
rendered=$(mktemp /tmp/saydin-production-compose.XXXXXX.json)
trap 'rm -f "$rendered"' EXIT HUP INT TERM

compose() {
  docker compose --project-name "$project" --env-file "$env_file" --file "$compose_file" "$@"
}

compose config --format json > "$rendered"
python3 infrastructure/deployment/validate-production.py "$rendered"
if ! python3 - "$rendered" <<'PY'
import json, sys
document=json.load(open(sys.argv[1], encoding="utf-8"))
command=document["services"]["data-quality-audit"].get("command", [])
text=" ".join(command if isinstance(command, list) else [str(command)])
required=("--signer-mode oci-kms-instance-principal", "--kms-key-id", "--kms-key-version-id",
          "--kms-crypto-endpoint", "--oci-region", "--evidence-public-key",
          "--allowed-evidence-key-ids", "--kms-timeout-seconds 10")
if "--evidence-private-key" in text or any(item not in text for item in required):
    raise SystemExit(1)
PY
then
  die "deployment_dqa_kms_contract_invalid" 78
fi
if [ "$environment" = production ] && [ "${SAYDIN_ENABLE_BACKUP-}" != true ]; then
  die "deployment_backup_required" 78
fi

compose up -d postgres redis otel-collector
backup_cidr=$(python3 - "$rendered" <<'PY'
import json, sys
value=json.load(open(sys.argv[1], encoding="utf-8"))["networks"]["backup-db"]["ipam"]["config"]
if len(value) != 1: raise SystemExit(1)
print(value[0]["subnet"])
PY
) || die "deployment_backup_network_contract_invalid" 78
role_prefix=$(python3 - "$rendered" <<'PY'
import json, sys
env=json.load(open(sys.argv[1], encoding="utf-8"))["services"]["database-backup"]["environment"]
print(env["SAYDIN_DATABASE_ROLE_PREFIX"])
PY
) || die "deployment_backup_role_contract_invalid" 78
api_allowed_host=$(python3 - "$rendered" <<'PY'
import json, sys
value=json.load(open(sys.argv[1], encoding="utf-8"))["services"]["saydin-api"]["environment"]["AllowedHosts"]
parts=value.split(";")
if len(parts) != 2 or parts[1] != "saydin-api": raise SystemExit(1)
print(parts[0])
PY
) || die "deployment_api_host_contract_invalid" 78
# Expanded inside the PostgreSQL container, not by the deployment shell.
# shellcheck disable=SC2016
hba_path=$(compose exec -T postgres sh -eu -c \
  'psql -X -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SHOW hba_file"' | tr -d '[:space:]')
case "$hba_path" in /*/pg_hba.conf) ;; *) die "deployment_hba_path_invalid" 78 ;; esac
compose exec -T --user 70:70 postgres python3 - install --hba "$hba_path" \
  --cidr "$backup_cidr" --role-prefix "$role_prefix" \
  < infrastructure/backup/manage_backup_hba.py
# Expanded inside the PostgreSQL container, not by the deployment shell.
# shellcheck disable=SC2016
hba_reload=$(compose exec -T postgres sh -eu -c \
  'psql -X -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -tAc \
    "SELECT pg_reload_conf(); SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL"' \
  | tr -d '[:space:]')
[ "$hba_reload" = t0 ] || die "deployment_hba_reload_failed"
compose exec -T --user 70:70 postgres python3 - verify --hba "$hba_path" \
  --cidr "$backup_cidr" --role-prefix "$role_prefix" \
  < infrastructure/backup/manage_backup_hba.py

compose up --no-deps --force-recreate --exit-code-from database-role-bootstrap database-role-bootstrap
prebootstrap_log=$(compose logs --no-color database-role-bootstrap)
prebootstrap_phase=$(printf '%s\n' "$prebootstrap_log" \
  | sed -n 's/.*backup_postbootstrap_required=\(true\|false\).*/\1/p' | sort -u)
case "$prebootstrap_phase" in true|false) ;; *) die "deployment_prebootstrap_phase_invalid" ;; esac
compose up --no-deps --force-recreate --exit-code-from database-migrator database-migrator
migration_log=$(compose logs --no-color database-migrator)
migration_phase=$(printf '%s\n' "$migration_log" \
  | sed -n 's/.*backup_postbootstrap_required=\(true\|false\).*/\1/p' | sort -u)
case "$migration_phase" in true|false) ;; *) die "deployment_migration_backup_phase_invalid" ;; esac
[ "$prebootstrap_phase" = "$migration_phase" ] || die "deployment_backup_phase_transition_invalid"

compose up --no-deps --force-recreate --exit-code-from database-role-bootstrap database-role-bootstrap
compose logs --no-color database-role-bootstrap | grep -q 'backup_postbootstrap_required=false' \
  || die "deployment_postbootstrap_phase_invalid"
migrator_verify=$(compose run --rm --no-deps database-migrator --verify-only 2>&1) \
  || die "deployment_migrator_verify_failed"
printf '%s\n' "$migrator_verify" | grep -q 'backup_postbootstrap_required=false' \
  || die "deployment_migrator_postbootstrap_invalid"

if [ "${SAYDIN_ENABLE_BACKUP-}" = true ]; then
  compose --profile backup run --rm --no-deps database-backup verify-auth
  compose --profile backup up -d --no-deps database-wal-archive database-backup
  compose --profile backup run --rm --no-deps database-backup base-backup
  [ -n "$(compose --profile backup ps --status running -q database-wal-archive)" ] \
    || die "deployment_wal_archive_not_running"
  [ -n "$(compose --profile backup ps --status running -q database-backup)" ] \
    || die "deployment_base_backup_scheduler_not_running"
fi

compose --profile audit up --no-deps --exit-code-from data-quality-audit data-quality-audit
compose --profile audit run --rm --no-deps data-quality-audit verify-evidence \
  --bundle /run/audit-output/evidence \
  --public-key /run/saydin-secrets/private/evidence-public.pem

compose up -d --no-deps saydin-api
attempt=0
until compose exec -T saydin-api curl -fsS -H "Host: $api_allowed_host" \
    http://127.0.0.1:8080/health/live >/dev/null 2>&1; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 30 ] || die "deployment_internal_smoke_failed"
  sleep 2
done

compose up -d --no-deps caddy
require_smoke=${SAYDIN_PUBLIC_SMOKE_CONFIG_FILE-}
case "$require_smoke" in /*) ;; *) die "deployment_public_smoke_config_required" 78 ;; esac
[ -f "$require_smoke" ] && [ ! -L "$require_smoke" ] || die "deployment_public_smoke_config_invalid" 78
mode=$(stat -c %a "$require_smoke" 2>/dev/null || stat -f %Lp "$require_smoke")
case "$mode" in 400|600) ;; *) die "deployment_public_smoke_config_mode" 78 ;; esac
curl --fail --silent --show-error --config "$require_smoke" >/dev/null

if [ "${SAYDIN_ENABLE_INGESTION-}" = true ]; then
  compose --profile ingestion up -d saydin-price-ingestion
fi
# The one control-plane session is excluded; any other admin/superuser backend blocks admission.
backend_count=$(compose exec -T postgres psql -X -A -t -v ON_ERROR_STOP=1 -U saydin_admin -d postgres -c \
  "SELECT count(*) FROM pg_stat_activity a JOIN pg_roles r ON r.oid=a.usesysid WHERE a.pid<>pg_backend_pid() AND (r.rolsuper OR r.rolname='saydin_admin');")
[ "$backend_count" = 0 ] || die "deployment_privileged_backend_present"

python3 - "$receipt_dir/receipt.json" "$environment" "$project" "$manifest_sha" "$workflow_run_id" <<'PY'
import datetime, json, sys
path, environment, project, digest, run_id = sys.argv[1:]
value = {
    "schemaVersion": 1,
    "environment": environment,
    "project": project,
    "workflowRunId": run_id,
    "manifestSha256": digest,
    "completedAt": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
    "status": "passed",
}
open(path, "w", encoding="utf-8").write(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n")
PY
printf '%s\n' "deployment_gate_passed:$manifest_sha"
