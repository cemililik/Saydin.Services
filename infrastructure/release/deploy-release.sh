#!/bin/sh
set -eu
umask 077

die() { printf '%s\n' "$1" >&2; exit "${2:-70}"; }
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P) || exit 70
repo_root=$(CDPATH='' cd -- "$script_dir/../.." && pwd -P) || exit 70
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

manifest_sha=$(python3 "$script_dir/release_manifest.py" verify --manifest "$release_dir/release-manifest.json")
if ! python3 "$script_dir/render-deployment-env.py" \
  --manifest "$release_dir/release-manifest.json" --verify-existing "$env_file"
then
  die "deployment_manifest_binding_failed" 78
fi
rendered=$(mktemp /tmp/saydin-production-compose.XXXXXX.json)
rules_response=$(mktemp /tmp/saydin-prometheus-rules.XXXXXX.json)
targets_response=$(mktemp /tmp/saydin-prometheus-targets.XXXXXX.json)
series_response=$(mktemp /tmp/saydin-prometheus-series.XXXXXX.json)
trap 'rm -f -- "$rendered" "$rules_response" "$targets_response" "$series_response"' EXIT HUP INT TERM

compose() {
  docker compose --project-name "$project" --env-file "$env_file" --file "$compose_file" "$@"
}

compose config --format json > "$rendered"
python3 "$repo_root/infrastructure/deployment/validate-production.py" "$rendered"
volume_contracts=$(python3 - "$rendered" <<'PY'
import json, re, sys
document=json.load(open(sys.argv[1], encoding="utf-8"))
volumes=document["volumes"]
private={
 "postgres_secret":("postgres","private"), "redis_secret":("redis","private"),
 "bootstrap_secret":("bootstrap","private"), "migrator_secret":("migrator","private"),
 "api_secret":("api","private"), "api_config":("api-config","root"),
 "ingestion_secret":("ingestion","private"), "ingestion_config":("ingestion-config","root"),
 "calendar_secret":("calendar","private"), "exporter_secret":("exporter","private"),
 "redis_exporter_secret":("redis-exporter","private"),
 "alertmanager_secret":("alertmanager","private"), "audit_secret":("audit","private"),
 "data_repair_secret":("data-repair","private"),
 "backup_secret":("backup","private"),
}
writable={
 "postgres_data":70, "redis_data":999, "caddy_data":1000, "caddy_config":1000,
 "prometheus_data":65534, "alertmanager_data":65534, "otel_queue":10001,
 "tempo_data":10001, "loki_data":10001, "backup_metrics":1001,
 "backup_base_staging":1001, "backup_wal_spool":1001, "calendar_data":1001,
 "audit_output":1001,
 "data_repair_receipts":1001,
}
for key,(purpose,location) in private.items():
 name=volumes[key]["name"]
 if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", name): raise SystemExit(1)
 print("private", purpose, location, name, sep="|")
for key,uid in writable.items():
 name=volumes[key]["name"]
 if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", name): raise SystemExit(1)
 print("runtime", uid, "root", name, sep="|")
name=volumes["blackbox_targets"]["name"]
if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", name): raise SystemExit(1)
print("blackbox", "65534", "root", name, sep="|")
PY
) || die "deployment_volume_contract_render_failed" 78
helper_image=$(python3 - "$rendered" <<'PY'
import json, sys
print(json.load(open(sys.argv[1], encoding="utf-8"))["services"]["postgres"]["image"])
PY
) || die "deployment_volume_helper_image_missing" 78
public_host=$(python3 - "$rendered" <<'PY'
import json, sys
print(json.load(open(sys.argv[1], encoding="utf-8"))["services"]["caddy"]["environment"]["SAYDIN_PUBLIC_HOST"])
PY
) || die "deployment_public_host_missing" 78
printf '%s\n' "$volume_contracts" | while IFS='|' read -r kind argument location volume_name; do
  docker volume inspect "$volume_name" >/dev/null 2>&1 \
    || die "deployment_external_volume_missing" 78
  case "$kind" in
    private)
      material_root=/material
      [ "$location" = private ] && material_root=/material/private
      docker run --rm --network none --read-only --cap-drop ALL \
        --security-opt no-new-privileges:true --user 0:0 --pids-limit 64 \
        --memory 128m --cpus 0.25 --tmpfs /tmp:mode=0700,size=8m \
        --mount "type=bind,src=$repo_root/infrastructure/deployment/validate-private-material.py,dst=/validator.py,readonly" \
        --mount "type=volume,src=$volume_name,dst=/material,readonly" \
        --entrypoint python3 "$helper_image" /validator.py "$argument" "$material_root" \
        || die "deployment_private_material_invalid" 78
      ;;
    runtime)
      docker run --rm --network none --read-only --cap-drop ALL \
        --security-opt no-new-privileges:true --user 0:0 --pids-limit 64 \
        --memory 128m --cpus 0.25 --tmpfs /tmp:mode=0700,size=8m \
        --mount "type=bind,src=$repo_root/infrastructure/deployment/validate-runtime-volume.py,dst=/validator.py,readonly" \
        --mount "type=volume,src=$volume_name,dst=/material,readonly" \
        --entrypoint python3 "$helper_image" /validator.py --uid "$argument" /material \
        || die "deployment_runtime_volume_invalid" 78
      ;;
    blackbox)
      docker run --rm --network none --read-only --cap-drop ALL \
        --security-opt no-new-privileges:true --user 0:0 --pids-limit 64 \
        --memory 128m --cpus 0.25 --tmpfs /tmp:mode=0700,size=8m \
        --mount "type=bind,src=$repo_root/infrastructure/deployment/validate-blackbox-targets.py,dst=/validator.py,readonly" \
        --mount "type=volume,src=$volume_name,dst=/material,readonly" \
        --entrypoint python3 "$helper_image" /validator.py --public-host "$public_host" /material \
        || die "deployment_blackbox_targets_invalid" 78
      ;;
    *) die "deployment_volume_contract_invalid" 78 ;;
  esac
done
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
backup_valid_until=$(python3 - "$rendered" <<'PY'
import datetime, json, sys
value=json.load(open(sys.argv[1], encoding="utf-8"))["services"]["database-backup"]["environment"]["SAYDIN_BACKUP_V1_VALID_UNTIL"]
parsed=datetime.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
print(int(parsed.timestamp()))
PY
) || die "deployment_backup_validity_format_invalid" 78
attempt=0
until compose exec -T postgres pg_isready -h 127.0.0.1 -d postgres >/dev/null 2>&1; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 60 ] || die "deployment_database_preflight_unavailable" 75
  sleep 2
done
backup_validity_window=$(compose exec -T postgres psql -X -A -t -v ON_ERROR_STOP=1 \
  -U saydin_admin -d postgres -c \
  "SELECT floor(extract(epoch FROM (to_timestamp($backup_valid_until) - clock_timestamp())))::bigint") \
  || die "deployment_backup_validity_clock_query_failed" 75
case "$backup_validity_window" in ""|*[!0-9]*) die "deployment_backup_validity_window_invalid" 78 ;; esac
[ "$backup_validity_window" -ge 3888000 ] && [ "$backup_validity_window" -le 8035200 ] || \
  die "deployment_backup_validity_window_unsafe" 78
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
  < "$repo_root/infrastructure/backup/manage_backup_hba.py"
# Expanded inside the PostgreSQL container, not by the deployment shell.
# shellcheck disable=SC2016
hba_reload=$(compose exec -T postgres sh -eu -c \
  'psql -X -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -tAc \
    "SELECT pg_reload_conf(); SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL"' \
  | tr -d '[:space:]')
[ "$hba_reload" = t0 ] || die "deployment_hba_reload_failed"
compose exec -T --user 70:70 postgres python3 - verify --hba "$hba_path" \
  --cidr "$backup_cidr" --role-prefix "$role_prefix" \
  < "$repo_root/infrastructure/backup/manage_backup_hba.py"

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
  backup_role="${role_prefix}_backup_login_v1"
  backup_role_validity=$(compose exec -T postgres psql -X -A -t -F '|' -v ON_ERROR_STOP=1 \
    -v backup_role="$backup_role" -U saydin_admin -d postgres -c \
    "SELECT floor(extract(epoch FROM clock_timestamp()))::bigint,
            floor(extract(epoch FROM rolvaliduntil))::bigint
       FROM pg_catalog.pg_roles WHERE rolname=:'backup_role'") \
    || die "deployment_backup_role_validity_query_failed" 78
  case "$backup_role_validity" in *'|'*) ;; *) die "deployment_backup_role_validity_invalid" 78 ;; esac
  backup_database_epoch=${backup_role_validity%%|*}
  backup_actual_valid_until=${backup_role_validity#*|}
  case "$backup_database_epoch:$backup_actual_valid_until" in
    *:*:*|*[!0-9:]*|:*|*:) die "deployment_backup_role_validity_invalid" 78 ;;
  esac
  [ "$backup_actual_valid_until" = "$backup_valid_until" ] \
    || die "deployment_backup_role_validity_mismatch" 78
  backup_actual_window=$((backup_actual_valid_until - backup_database_epoch))
  [ "$backup_actual_window" -ge 3888000 ] && [ "$backup_actual_window" -le 8035200 ] \
    || die "deployment_backup_role_validity_unsafe" 78
  compose --profile backup run --rm --no-deps \
    -e SAYDIN_BACKUP_ROLE_VALID_UNTIL_EPOCH_SECONDS="$backup_actual_valid_until" \
    database-backup verify-auth
  compose --profile backup up -d --no-deps database-wal-archive
  compose --profile backup run --rm --no-deps database-backup base-backup
  compose --profile backup up -d --no-deps database-backup
  [ -n "$(compose --profile backup ps --status running -q database-wal-archive)" ] \
    || die "deployment_wal_archive_not_running"
  [ -n "$(compose --profile backup ps --status running -q database-backup)" ] \
    || die "deployment_base_backup_scheduler_not_running"
fi

compose --profile audit up --no-deps --exit-code-from data-quality-audit data-quality-audit
compose --profile audit run --rm --no-deps data-quality-audit verify-evidence \
  --bundle /run/audit-output/evidence \
  --public-key /run/saydin-secrets/private/evidence-public.pem

# Validate every mutable monitoring config with the candidate binaries before
# force-recreating the currently healthy control plane.
compose run --rm --no-deps --entrypoint promtool prometheus \
  check config /etc/prometheus/prometheus.yml
compose run --rm --no-deps --entrypoint amtool alertmanager \
  check-config /run/saydin-secrets/private/alertmanager.yml
compose run --rm --no-deps --entrypoint /otelcol-contrib otel-collector \
  validate --config=/etc/otelcol/config.yml
compose run --rm --no-deps --entrypoint /tempo tempo \
  -config.file=/etc/tempo/config.yml -config.expand-env=true -config.verify=true
compose run --rm --no-deps --entrypoint /usr/bin/loki loki \
  -config.file=/etc/loki/config.yml -config.expand-env=true -verify-config

compose up -d --no-deps --force-recreate \
  alertmanager tempo loki otel-collector postgres-exporter redis-exporter \
  blackbox-exporter node-exporter prometheus
attempt=0
# Expanded inside the Prometheus container, not by the deployment shell.
# shellcheck disable=SC2016
until compose exec -T prometheus sh -eu -c '
  promtool check healthy --url=http://127.0.0.1:9090 >/dev/null
  for endpoint in \
    http://alertmanager:9093/-/ready \
    http://otel-collector:13133/ \
    http://tempo:3200/ready \
    http://loki:3100/ready \
    http://postgres-exporter:9187/metrics \
    http://redis-exporter:9121/metrics \
    http://blackbox-exporter:9115/metrics \
    http://node-exporter:9100/metrics
  do wget -q --spider "$endpoint"; done
'; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 60 ] || die "deployment_monitoring_readiness_failed"
  sleep 2
done

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
validate_monitoring_runtime() {
  if [ "${SAYDIN_ENABLE_INGESTION-}" = true ]; then
    python3 "$repo_root/infrastructure/deployment/validate-prometheus-runtime.py" \
      --rule-root "$repo_root/infrastructure/prometheus/rules" \
      --rules-response "$rules_response" --targets-response "$targets_response" \
      --series-response "$series_response" \
      --expected-probe "https://$public_host/health/live" --require-ingestion
  else
    python3 "$repo_root/infrastructure/deployment/validate-prometheus-runtime.py" \
      --rule-root "$repo_root/infrastructure/prometheus/rules" \
      --rules-response "$rules_response" --targets-response "$targets_response" \
      --series-response "$series_response" \
      --expected-probe "https://$public_host/health/live"
  fi
}
fetch_monitoring_runtime() {
  series_end=$(date +%s)
  series_start=$((series_end - 300))
  case "$series_start:$series_end" in *[!0-9:]*) return 1 ;; esac
  compose exec -T prometheus wget -qO- \
    'http://127.0.0.1:9090/api/v1/rules?type=alert' > "$rules_response" \
    && compose exec -T prometheus wget -qO- \
    'http://127.0.0.1:9090/api/v1/targets?state=active' > "$targets_response" \
    && compose exec -T prometheus wget -qO- \
    "http://127.0.0.1:9090/api/v1/series?match%5B%5D=%7B__name__%3D~%22saydin_activity_log_.*%7Csaydin_process_start_time_seconds%7Chttp_server_request_duration_seconds_count%7Csaydin_market_calendar_coverage_horizon_days%22%7D&start=$series_start&end=$series_end" \
      > "$series_response"
}
attempt=0
until fetch_monitoring_runtime \
    && validate_monitoring_runtime >/dev/null 2>&1; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 60 ] || {
    validate_monitoring_runtime || true
    die "deployment_prometheus_runtime_contract_failed" 78
  }
  sleep 2
done
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
