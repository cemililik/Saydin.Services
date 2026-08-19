#!/bin/sh
set -eu
umask 077

die() { printf '%s\n' "$1" >&2; exit "${2:-70}"; }
[ "$#" -eq 14 ] || die "rollback_usage" 64
project=$1
compose_file=$2
current_env=$3
target_env=$4
current_release_dir=$5
target_release_dir=$6
receipt_dir=$7
workflow_run_id=$8
incident_id=$9
shift 9
current_release_tag=$1
target_release_tag=$2
repository=$3
current_commit=$4
target_commit=$5

[ "$project" = saydin-production ] || die "rollback_project_guard_failed" 64
case "$workflow_run_id" in ""|*[!0-9]*) die "rollback_workflow_run_id_invalid" 64 ;; esac
case "$incident_id" in ""|*[!A-Za-z0-9._-]*) die "rollback_incident_id_invalid" 64 ;; esac
case "$repository" in
  */*) case "$repository" in *[!A-Za-z0-9._/-]*|/*|*/|*//*|*/*/*) die "rollback_repository_invalid" 64 ;; esac ;;
  *) die "rollback_repository_invalid" 64 ;;
esac
for commit in "$current_commit" "$target_commit"; do
  case "$commit" in ""|*[!0-9a-f]*) die "rollback_commit_invalid" 64 ;; esac
  [ "$(printf %s "$commit" | wc -c | tr -d ' ')" = 40 ] || die "rollback_commit_invalid" 64
done
incident_length=$(printf %s "$incident_id" | wc -c | tr -d ' ')
[ "$incident_length" -ge 3 ] && [ "$incident_length" -le 64 ] || die "rollback_incident_id_invalid" 64
for path in "$compose_file" "$current_env" "$target_env" \
  "$current_release_dir/release-manifest.json" "$target_release_dir/release-manifest.json"; do
  case "$path" in /*) ;; *) die "rollback_absolute_path_required" 64 ;; esac
  [ -f "$path" ] || die "rollback_input_missing" 66
done
identity=$(python3 - "$repository" <<'PY'
import re, sys
print(r"^https://github.com/" + re.escape(sys.argv[1])
      + r"/\.github/workflows/release-images\.yml@refs/heads/main$")
PY
) || die "rollback_identity_contract_failed" 78
infrastructure/release/verify-signed-release.sh "$current_release_dir" "$identity" \
  https://token.actions.githubusercontent.com "$repository" "$current_release_tag" "$current_commit" \
  || die "rollback_current_signature_invalid" 78
infrastructure/release/verify-signed-release.sh "$target_release_dir" "$identity" \
  https://token.actions.githubusercontent.com "$repository" "$target_release_tag" "$target_commit" \
  || die "rollback_target_signature_invalid" 78

current_sha=$(python3 infrastructure/release/release_manifest.py verify \
  --manifest "$current_release_dir/release-manifest.json")
target_sha=$(python3 infrastructure/release/release_manifest.py verify \
  --manifest "$target_release_dir/release-manifest.json")
python3 infrastructure/release/release_manifest.py verify-rollback \
  --current "$current_release_dir/release-manifest.json" \
  --target "$target_release_dir/release-manifest.json" >/dev/null

case "$receipt_dir" in /*) ;; *) die "rollback_absolute_path_required" 64 ;; esac
[ ! -e "$receipt_dir" ] || die "rollback_receipt_target_exists" 73
mkdir -m 0700 "$receipt_dir"

verify_binding() {
  python3 - "$1" "$2" <<'PY'
import json, sys

manifest = json.load(open(sys.argv[1], encoding="utf-8"))
values = {}
for number, line in enumerate(open(sys.argv[2], encoding="utf-8"), 1):
    line = line.rstrip("\n")
    if not line or line.startswith("#"):
        continue
    key, separator, value = line.partition("=")
    if not separator or key in values:
        raise SystemExit("rollback_env_invalid")
    values[key] = value
keys = {"api":"SAYDIN_API_IMAGE", "ingestion":"SAYDIN_INGESTION_IMAGE", "caddy":"SAYDIN_CADDY_IMAGE"}
expected = {keys[item["name"]]: item["reference"] + "@" + item["digest"]
            for item in manifest["images"] if item["name"] in keys}
if any(values.get(key) != value for key, value in expected.items()):
    raise SystemExit("rollback_image_binding_mismatch")
if values.get("SAYDIN_GIT_SHA") != manifest["source"]["commitSha"]:
    raise SystemExit("rollback_source_binding_mismatch")
if values.get("SAYDIN_RELEASE_VERSION") != manifest["releaseId"]:
    raise SystemExit("rollback_release_binding_mismatch")
if values.get("SAYDIN_SERVICE_VERSION") != manifest["releaseId"]:
    raise SystemExit("rollback_service_version_binding_mismatch")
PY
}
verify_binding "$current_release_dir/release-manifest.json" "$current_env" || die "rollback_current_binding_failed" 78
verify_binding "$target_release_dir/release-manifest.json" "$target_env" || die "rollback_target_binding_failed" 78

current_compose() {
  docker compose --project-name "$project" --env-file "$current_env" --file "$compose_file" "$@"
}
target_compose() {
  docker compose --project-name "$project" --env-file "$target_env" --file "$compose_file" "$@"
}

for flavor in current target; do
  rendered=$(mktemp "/tmp/saydin-rollback-$flavor.XXXXXX.json")
  if [ "$flavor" = current ]; then current_compose config --format json > "$rendered"
  else target_compose config --format json > "$rendered"; fi
  python3 infrastructure/deployment/validate-production.py "$rendered" >/dev/null
  if ! python3 - "$rendered" <<'PY'
import json, sys
command=json.load(open(sys.argv[1], encoding="utf-8"))["services"]["data-quality-audit"].get("command", [])
text=" ".join(command if isinstance(command,list) else [str(command)])
required=("--signer-mode oci-kms-instance-principal", "--kms-key-id", "--kms-key-version-id",
          "--kms-crypto-endpoint", "--oci-region", "--evidence-public-key",
          "--allowed-evidence-key-ids", "--kms-timeout-seconds 10")
if "--evidence-private-key" in text or any(item not in text for item in required): raise SystemExit(1)
PY
  then
    rm -f "$rendered"
    die "rollback_dqa_kms_contract_invalid" 78
  fi
  rm -f "$rendered"
done
[ "${SAYDIN_ENABLE_BACKUP-}" = true ] || die "rollback_backup_required" 78
[ -n "$(current_compose --profile backup ps --status running -q database-wal-archive)" ] \
  || die "rollback_wal_archive_not_running" 78
[ -n "$(current_compose --profile backup ps --status running -q database-backup)" ] \
  || die "rollback_base_backup_scheduler_not_running" 78

expected_image() {
  key=$1
  sed -n "s/^$key=//p" "$current_env"
}
for pair in "saydin-api:SAYDIN_API_IMAGE" "caddy:SAYDIN_CADDY_IMAGE"; do
  service=${pair%%:*}
  key=${pair#*:}
  container=$(current_compose ps -q "$service")
  [ -n "$container" ] || die "rollback_current_service_missing:$service" 78
  actual=$(docker inspect --format '{{.Config.Image}}' "$container")
  [ "$actual" = "$(expected_image "$key")" ] || die "rollback_current_image_mismatch:$service" 78
done

current_compose run --rm --no-deps database-migrator --verify-only
current_compose --profile audit up --no-deps --exit-code-from data-quality-audit data-quality-audit

mutated=false
recover_current() {
  status=$?
  trap - EXIT HUP INT TERM
  if [ "$status" -ne 0 ] && [ "$mutated" = true ]; then
    current_compose up -d --no-deps saydin-api caddy >/dev/null 2>&1 || true
    if [ "${SAYDIN_ENABLE_INGESTION-}" = true ]; then
      current_compose --profile ingestion up -d --no-deps saydin-price-ingestion >/dev/null 2>&1 || true
    fi
  fi
  exit "$status"
}
trap recover_current EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

mutated=true
target_compose up -d --no-deps saydin-api
attempt=0
until target_compose exec -T saydin-api curl -fsS http://127.0.0.1:8080/health/live >/dev/null 2>&1; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 30 ] || die "rollback_internal_smoke_failed"
  sleep 2
done
target_compose up -d --no-deps caddy
smoke=${SAYDIN_PUBLIC_SMOKE_CONFIG_FILE-}
case "$smoke" in /*) ;; *) die "rollback_public_smoke_config_required" 78 ;; esac
[ -f "$smoke" ] && [ ! -L "$smoke" ] || die "rollback_public_smoke_config_invalid" 78
mode=$(stat -c %a "$smoke" 2>/dev/null || stat -f %Lp "$smoke")
case "$mode" in 400|600) ;; *) die "rollback_public_smoke_config_mode" 78 ;; esac
curl --fail --silent --show-error --config "$smoke" >/dev/null
if [ "${SAYDIN_ENABLE_INGESTION-}" = true ]; then
  target_compose --profile ingestion up -d --no-deps saydin-price-ingestion
fi
backend_count=$(target_compose exec -T postgres psql -X -A -t -v ON_ERROR_STOP=1 \
  -U saydin_admin -d postgres -c \
  "SELECT count(*) FROM pg_stat_activity a JOIN pg_roles r ON r.oid=a.usesysid WHERE a.pid<>pg_backend_pid() AND (r.rolsuper OR r.rolname='saydin_admin');")
[ "$backend_count" = 0 ] || die "rollback_privileged_backend_present"

python3 - "$receipt_dir/receipt.json" "$workflow_run_id" "$incident_id" \
  "$current_release_tag" "$target_release_tag" "$current_sha" "$target_sha" <<'PY'
import datetime, json, sys
path, run_id, incident, current_tag, target_tag, current_sha, target_sha = sys.argv[1:]
value={"schemaVersion":1,"workflowRunId":run_id,"incidentId":incident,
       "currentRelease":current_tag,"targetRelease":target_tag,
       "currentManifestSha256":current_sha,"targetManifestSha256":target_sha,
       "operation":"application-only","status":"passed",
       "completedAt":datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00","Z")}
open(path,"w",encoding="utf-8").write(json.dumps(value,sort_keys=True,separators=(",",":"))+"\n")
PY
mutated=false
printf '%s\n' "rollback_gate_passed:$target_sha"
