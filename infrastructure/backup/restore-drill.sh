#!/bin/sh
# Destructive operations are confined to exact disposable Docker resources and an
# openat-validated restore target. Evidence signing uses OCI instance principal KMS.
set -eu
umask 077

die() { printf '%s\n' "$1" >&2; exit "${2:-70}"; }
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P) || exit 70
repo_root=$(CDPATH='' cd -- "$script_dir/../.." && pwd -P) || exit 70
[ "$#" -eq 7 ] || die "restore_drill_usage" 64
run_id=$1
run_attempt=$2
target_time=$3
manifest=$4
deployment_env=$5
contract_env=$6
evidence_dir=$7
case "$run_id" in ""|*[!0-9]*) die "restore_run_id_invalid" 64 ;; esac
case "$run_attempt" in ""|*[!0-9]*) die "restore_run_attempt_invalid" 64 ;; esac
python3 - "$target_time" <<'PY' || die "restore_target_time_invalid" 64
import datetime, sys
datetime.datetime.strptime(sys.argv[1], "%Y-%m-%dT%H:%M:%SZ")
PY
for path in "$manifest" "$deployment_env" "$contract_env"; do
  case "$path" in /*) ;; *) die "restore_input_absolute_required" 64 ;; esac
  [ -f "$path" ] || die "restore_input_missing" 66
done
case "$evidence_dir" in /*) ;; *) die "restore_evidence_absolute_required" 64 ;; esac
[ ! -e "$evidence_dir" ] || die "restore_evidence_target_exists" 73
mkdir -m 0700 "$evidence_dir"

env_value() {
  python3 - "$1" "$2" <<'PY'
import sys
path, wanted = sys.argv[1:]
seen = {}
for line in open(path, encoding="utf-8"):
    line = line.rstrip("\n")
    if not line or line.startswith("#"): continue
    key, sep, value = line.partition("=")
    if not sep or key in seen: raise SystemExit(2)
    seen[key] = value
value = seen.get(wanted, "")
if not value: raise SystemExit(2)
print(value)
PY
}

python3 "$repo_root/infrastructure/release/release_manifest.py" verify --manifest "$manifest" >/dev/null
backup_image=$(env_value "$deployment_env" SAYDIN_BACKUP_IMAGE)
control_image=$(env_value "$deployment_env" SAYDIN_CONTROL_IMAGE)
dqa_image=$(env_value "$deployment_env" SAYDIN_DQA_IMAGE)
api_image=$(env_value "$deployment_env" SAYDIN_API_IMAGE)
timescale_image=$(env_value "$deployment_env" SAYDIN_TIMESCALE_IMAGE)
redis_image=$(env_value "$deployment_env" SAYDIN_REDIS_IMAGE)
database=$(env_value "$deployment_env" SAYDIN_DATABASE)
deployment=$(env_value "$deployment_env" SAYDIN_DEPLOYMENT_ID)
system_id=$(env_value "$deployment_env" SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256)
prefix=$(env_value "$deployment_env" SAYDIN_DATABASE_ROLE_PREFIX)
migrator_login=$(env_value "$deployment_env" SAYDIN_MIGRATOR_LOGIN)
audit_login=$(env_value "$deployment_env" SAYDIN_AUDIT_LOGIN)
api_login=$(env_value "$deployment_env" SAYDIN_API_LOGIN)
repository=$(env_value "$deployment_env" SAYDIN_BACKUP_REPOSITORY)
bucket=$(env_value "$deployment_env" SAYDIN_BACKUP_BUCKET)
role_arn=$(env_value "$deployment_env" SAYDIN_BACKUP_OBJECT_STORE_ROLE)
region=$(env_value "$deployment_env" SAYDIN_BACKUP_REGION)
kms_key_id=$(env_value "$deployment_env" SAYDIN_BACKUP_KMS_KEY_ID)
dqa_kms_key_id=$(env_value "$deployment_env" SAYDIN_DQA_KMS_KEY_ID)
dqa_kms_key_version_id=$(env_value "$deployment_env" SAYDIN_DQA_KMS_KEY_VERSION_ID)
dqa_kms_crypto_endpoint=$(env_value "$deployment_env" SAYDIN_DQA_KMS_CRYPTO_ENDPOINT)
dqa_oci_region=$(env_value "$deployment_env" SAYDIN_DQA_OCI_REGION)
dqa_allowed_evidence_key_ids=$(env_value "$deployment_env" SAYDIN_DQA_ALLOWED_EVIDENCE_KEY_IDS)
backup_v1_valid_until=$(env_value "$deployment_env" SAYDIN_BACKUP_V1_VALID_UNTIL)
public_host=$(env_value "$deployment_env" SAYDIN_PUBLIC_HOST)
proxy_network=$(env_value "$deployment_env" SAYDIN_PROXY_NETWORK_CIDR)
release_version=$(env_value "$deployment_env" SAYDIN_RELEASE_VERSION)
service_version=$(env_value "$deployment_env" SAYDIN_SERVICE_VERSION)
git_sha=$(env_value "$deployment_env" SAYDIN_GIT_SHA)

for key in BACKUP_SECRET_DIR BOOTSTRAP_SECRET_DIR MIGRATOR_SECRET_DIR AUDIT_SECRET_DIR AUDIT_INPUT_DIR AUDIT_OUTPUT_DIR API_SECRET_DIR API_CONFIG_FILE REDIS_SECRET_DIR GEOIP_DIR; do
  value=$(env_value "$contract_env" "SAYDIN_RESTORE_$key")
  case "$value" in /*) ;; *) die "restore_contract_path_invalid" 78 ;; esac
  [ -e "$value" ] || die "restore_contract_path_missing" 78
done
backup_secret=$(env_value "$contract_env" SAYDIN_RESTORE_BACKUP_SECRET_DIR)
bootstrap_secret=$(env_value "$contract_env" SAYDIN_RESTORE_BOOTSTRAP_SECRET_DIR)
migrator_secret=$(env_value "$contract_env" SAYDIN_RESTORE_MIGRATOR_SECRET_DIR)
audit_secret=$(env_value "$contract_env" SAYDIN_RESTORE_AUDIT_SECRET_DIR)
audit_input=$(env_value "$contract_env" SAYDIN_RESTORE_AUDIT_INPUT_DIR)
audit_output=$(env_value "$contract_env" SAYDIN_RESTORE_AUDIT_OUTPUT_DIR)
api_secret=$(env_value "$contract_env" SAYDIN_RESTORE_API_SECRET_DIR)
api_config=$(env_value "$contract_env" SAYDIN_RESTORE_API_CONFIG_FILE)
redis_secret=$(env_value "$contract_env" SAYDIN_RESTORE_REDIS_SECRET_DIR)
geoip=$(env_value "$contract_env" SAYDIN_RESTORE_GEOIP_DIR)

[ -d "$audit_output" ] && [ ! -L "$audit_output" ] || die "restore_audit_output_invalid" 78
audit_owner=$(stat -c %u "$audit_output" 2>/dev/null || stat -f %u "$audit_output")
audit_group=$(stat -c %g "$audit_output" 2>/dev/null || stat -f %g "$audit_output")
audit_mode=$(stat -c %a "$audit_output" 2>/dev/null || stat -f %Lp "$audit_output")
[ "$audit_owner:$audit_group:$audit_mode" = 1001:1001:700 ] || die "restore_audit_output_permissions_invalid" 78
audit_run_output="$audit_output/$run_id-$run_attempt"
case "$audit_run_output" in "$audit_output"/[0-9]*-[0-9]*) ;; *) die "restore_audit_output_guard_failed" 78 ;; esac
[ ! -e "$audit_run_output" ] || die "restore_audit_run_exists" 73
mkdir -m 0700 "$audit_run_output" || die "restore_audit_run_create_failed" 73
run_owner=$(stat -c %u "$audit_run_output" 2>/dev/null || stat -f %u "$audit_run_output")
run_group=$(stat -c %g "$audit_run_output" 2>/dev/null || stat -f %g "$audit_run_output")
run_mode=$(stat -c %a "$audit_run_output" 2>/dev/null || stat -f %Lp "$audit_run_output")
[ "$run_owner:$run_group:$run_mode" = 1001:1001:700 ] || die "restore_audit_run_permissions_invalid" 78

prefix_name="saydin-restore-$run_id-$run_attempt"
network="$prefix_name-net"
egress_network="$prefix_name-egress"
volume="$prefix_name-data"
database_container="$prefix_name-db"
redis_container="$prefix_name-redis"
api_container="$prefix_name-api"
dqa_container="$prefix_name-dqa"
init_container="$prefix_name-init"
fetch_container="$prefix_name-fetch"
evidence_copy_container="$prefix_name-evidence-copy"
prepare_container="$prefix_name-prepare"
transaction_container="$prefix_name-transaction"
recovery_state_container="$prefix_name-recovery-state"
role_container="$prefix_name-role"
migrator_container="$prefix_name-migrator"
evidence_verify_container="$prefix_name-evidence-verify"
case "$prefix_name" in saydin-restore-*) ;; *) die "restore_resource_guard_failed" 78 ;; esac

resources_admitted=false

docker_reachable() { docker info >/dev/null 2>&1; }
container_exists() {
  docker container inspect "$1" >/dev/null 2>&1 && return 0
  docker_reachable || return 2
  return 1
}
volume_exists() {
  docker volume inspect "$1" >/dev/null 2>&1 && return 0
  docker_reachable || return 2
  return 1
}
network_exists() {
  docker network inspect "$1" >/dev/null 2>&1 && return 0
  docker_reachable || return 2
  return 1
}

docker_reachable || die "restore_docker_unavailable" 75
for resource in "$database_container" "$redis_container" "$api_container" "$dqa_container" \
  "$init_container" "$fetch_container" "$evidence_copy_container" "$prepare_container" \
  "$transaction_container" "$recovery_state_container" "$role_container" \
  "$migrator_container" "$evidence_verify_container"; do
  if container_exists "$resource"; then die "restore_resource_preexists:$resource" 73
  else [ "$?" -eq 1 ] || die "restore_docker_unavailable" 75; fi
done
if volume_exists "$volume"; then die "restore_resource_preexists:$volume" 73
else [ "$?" -eq 1 ] || die "restore_docker_unavailable" 75; fi
if network_exists "$network"; then die "restore_resource_preexists:$network" 73
else [ "$?" -eq 1 ] || die "restore_docker_unavailable" 75; fi
if network_exists "$egress_network"; then die "restore_resource_preexists:$egress_network" 73
else [ "$?" -eq 1 ] || die "restore_docker_unavailable" 75; fi
resources_admitted=true

remove_owned_resource() {
  kind=$1
  name=$2
  attempt=0
  while [ "$attempt" -lt 10 ]; do
    case "$kind" in
      container)
        if container_exists "$name"; then docker rm -f "$name" >/dev/null 2>&1 || true
        else state=$?; [ "$state" -eq 1 ] && return 0; return 1; fi ;;
      volume)
        if volume_exists "$name"; then docker volume rm "$name" >/dev/null 2>&1 || true
        else state=$?; [ "$state" -eq 1 ] && return 0; return 1; fi ;;
      network)
        if network_exists "$name"; then docker network rm "$name" >/dev/null 2>&1 || true
        else state=$?; [ "$state" -eq 1 ] && return 0; return 1; fi ;;
      *) return 1 ;;
    esac
    attempt=$((attempt + 1))
    sleep 1
  done
  return 1
}

cleanup() {
  original_status=$1
  trap - EXIT HUP INT TERM
  cleanup_failed=false
  if [ "$resources_admitted" = true ]; then
    remove_owned_resource container "$dqa_container" || cleanup_failed=true
    remove_owned_resource container "$api_container" || cleanup_failed=true
    remove_owned_resource container "$redis_container" || cleanup_failed=true
    remove_owned_resource container "$database_container" || cleanup_failed=true
    remove_owned_resource container "$init_container" || cleanup_failed=true
    remove_owned_resource container "$fetch_container" || cleanup_failed=true
    remove_owned_resource container "$evidence_copy_container" || cleanup_failed=true
    remove_owned_resource container "$prepare_container" || cleanup_failed=true
    remove_owned_resource container "$transaction_container" || cleanup_failed=true
    remove_owned_resource container "$recovery_state_container" || cleanup_failed=true
    remove_owned_resource container "$role_container" || cleanup_failed=true
    remove_owned_resource container "$migrator_container" || cleanup_failed=true
    remove_owned_resource container "$evidence_verify_container" || cleanup_failed=true
    remove_owned_resource volume "$volume" || cleanup_failed=true
    remove_owned_resource network "$network" || cleanup_failed=true
    remove_owned_resource network "$egress_network" || cleanup_failed=true
  fi
  if [ "$cleanup_failed" = true ]; then
    printf '%s\n' restore_cleanup_residual >&2
    [ "$original_status" -ne 0 ] || original_status=70
  fi
  [ "$original_status" -ne 0 ] || printf '%s\n' restore_drill_passed
  exit "$original_status"
}
trap 'cleanup $?' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

docker network create --internal "$network" >/dev/null
docker network create "$egress_network" >/dev/null
docker volume create "$volume" >/dev/null
docker run --rm --name "$init_container" --user 0:0 --read-only --cap-drop ALL --cap-add CHOWN --security-opt no-new-privileges \
  -v "$volume:/restore-drill" --entrypoint /bin/sh "$backup_image" \
  -c 'chmod 0700 /restore-drill && chown 1001:1001 /restore-drill'

docker run --rm --name "$fetch_container" --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  --network "$egress_network" -v "$volume:/restore-drill" -v "$backup_secret:/run/saydin-secrets/private:ro" \
  --tmpfs /tmp:uid=1001,gid=1001,mode=0700,size=512m \
  -e RESTIC_REPOSITORY="$repository" -e RESTIC_PASSWORD_FILE=/run/saydin-secrets/private/repository-password \
  -e AWS_WEB_IDENTITY_TOKEN_FILE=/run/saydin-secrets/private/object-store-token \
  -e AWS_ROLE_ARN="$role_arn" -e AWS_REGION="$region" \
  -e SAYDIN_BACKUP_BUCKET="$bucket" -e SAYDIN_DEPLOYMENT_ID="$deployment" \
  -e SAYDIN_BACKUP_KMS_KEY_ID="$kms_key_id" \
  -e SAYDIN_BACKUP_RPO_MINUTES=15 -e SAYDIN_BACKUP_RTO_MINUTES=120 \
  -e SAYDIN_RESTORE_TARGET=/restore-drill/work -e SAYDIN_RESTORE_CONFIRM=DISPOSABLE_RESTORE_ONLY \
  -e SAYDIN_RESTORE_TARGET_TIME="$target_time" "$backup_image" restore >/dev/null
docker run --rm --name "$evidence_copy_container" --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  -v "$volume:/restore-drill:ro" --entrypoint /bin/sh "$backup_image" \
  -c 'exec cat /restore-drill/wal-recovery-evidence.json' \
  > "$evidence_dir/wal-recovery-evidence.json"
docker run --rm --name "$prepare_container" --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  -v "$volume:/restore-drill" --entrypoint /usr/local/bin/saydin-prepare-recovery \
  "$backup_image" /restore-drill/pgdata "$target_time" >/dev/null

docker run -d --name "$database_container" --network "$network" --network-alias restored-db \
  --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:uid=1001,gid=1001,mode=0700 --tmpfs /run/postgresql:uid=1001,gid=1001,mode=0700 \
  -v "$volume:/restore-drill" -e PGDATA=/restore-drill/pgdata \
  --entrypoint postgres "$timescale_image" -D /restore-drill/pgdata >/dev/null
attempt=0
until docker exec "$database_container" pg_isready -h 127.0.0.1 -d "$database" >/dev/null 2>&1; do
  attempt=$((attempt + 1)); [ "$attempt" -lt 90 ] || die "restore_database_start_failed"; sleep 2
done
attempt=0
while :; do
  recovery_state=$(docker run --rm --name "$recovery_state_container" --network "$network" \
    --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
    -v "$bootstrap_secret:/run/private:ro" -e PGPASSFILE=/run/private/admin-pgpass \
    --entrypoint psql "$backup_image" -X -A -t -v ON_ERROR_STOP=1 \
    -h restored-db -p 5432 -U saydin_admin -d "$database" \
    -c 'SELECT NOT pg_is_in_recovery();' 2>/dev/null || true)
  [ "$recovery_state" != t ] || break
  if [ "$recovery_state" != f ]; then
    database_running=$(docker container inspect --format '{{.State.Running}}' "$database_container" 2>/dev/null) \
      || die "restore_recovery_target_unreachable" 78
    [ "$database_running" = true ] || die "restore_recovery_target_unreachable" 78
  fi
  attempt=$((attempt + 1))
  [ "$attempt" -lt 720 ] || die "restore_recovery_target_timeout" 75
  sleep 5
done

last_replayed_transaction_at=$(docker run --rm --name "$transaction_container" --network "$network" --user 1001:1001 --read-only --cap-drop ALL \
  --security-opt no-new-privileges -v "$bootstrap_secret:/run/private:ro" \
  -e PGPASSFILE=/run/private/admin-pgpass --entrypoint psql "$backup_image" \
  -X -A -t -v ON_ERROR_STOP=1 -h restored-db -p 5432 -U saydin_admin -d "$database" \
  -c "SELECT to_char(pg_last_xact_replay_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"');")
case "$last_replayed_transaction_at" in ""|????-??-??T??:??:??Z) ;; *) die "restore_transaction_timestamp_invalid" 78 ;; esac

docker run --rm --name "$role_container" --network "$network" --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  -v "$bootstrap_secret:/run/private:ro" --entrypoint dotnet "$control_image" \
  Saydin.DatabaseRoleBootstrap.dll verify --admin-connection-file /run/private/admin-connection \
  --deployment-id "$deployment" --target-database "$database" --system-identifier-sha256 "$system_id" \
  --role-prefix "$prefix" --timescaledb-version 2.16.1 --uuid-ossp-version 1.1 \
  --backup-v1-valid-until "$backup_v1_valid_until" >/dev/null

docker run --rm --name "$migrator_container" --network "$network" --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  -v "$migrator_secret:/run/saydin-secrets/private:ro" \
  -e PGHOST=restored-db -e PGPORT=5432 -e PGDATABASE="$database" -e PGUSER="$migrator_login" -e PGSSLMODE=Disable \
  -e SAYDIN_MIGRATOR_PASSWORD_FILE=/run/saydin-secrets/private/password \
  -e SAYDIN_DEPLOYMENT_ID="$deployment" -e SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256="$system_id" \
  -e SAYDIN_DATABASE_ROLE_PREFIX="$prefix" -e SAYDIN_DATABASE_LOGIN_VERSION=1 \
  -e SAYDIN_BACKUP_V1_VALID_UNTIL="$backup_v1_valid_until" \
  "$control_image" --verify-only >/dev/null

docker create --name "$dqa_container" --network "$network" --user 1001:1001 --read-only \
  --cap-drop ALL --security-opt no-new-privileges \
  -v "$audit_secret:/run/private:ro" -v "$audit_input:/run/input:ro" -v "$audit_output:/run/output" \
  -e PGHOST=restored-db -e PGPORT=5432 -e PGDATABASE="$database" -e PGUSER="$audit_login" -e PGSSLMODE=Disable \
  -e SAYDIN_AUDIT_DATABASE_PASSWORD_FILE=/run/private/password \
  -e SAYDIN_DEPLOYMENT_ID="$deployment" -e SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256="$system_id" \
  -e SAYDIN_DATABASE_ROLE_PREFIX="$prefix" -e SAYDIN_DATABASE_LOGIN_VERSION=1 \
  -e SAYDIN_BACKUP_V1_VALID_UNTIL="$backup_v1_valid_until" \
  "$dqa_image" scan --input /run/input/manifest.json --input-signature /run/input/manifest.sig \
  --input-public-key /run/input/input-public.pem --signer-mode oci-kms-instance-principal \
  --kms-key-id "$dqa_kms_key_id" --kms-key-version-id "$dqa_kms_key_version_id" \
  --kms-crypto-endpoint "$dqa_kms_crypto_endpoint" --oci-region "$dqa_oci_region" \
  --evidence-public-key /run/private/evidence-public.pem \
  --allowed-evidence-key-ids "$dqa_allowed_evidence_key_ids" --kms-timeout-seconds 10 \
  --hmac-key-file /run/private/evidence-hmac --output "/run/output/$run_id-$run_attempt/evidence" >/dev/null
docker network connect "$egress_network" "$dqa_container"
docker start -a "$dqa_container" >/dev/null || die "restore_dqa_failed"
docker rm "$dqa_container" >/dev/null
docker run --rm --name "$evidence_verify_container" --network none --user 1001:1001 --read-only --cap-drop ALL \
  --security-opt no-new-privileges -v "$audit_secret:/run/private:ro" \
  -v "$audit_output:/run/output:ro" "$dqa_image" verify-evidence \
  --bundle "/run/output/$run_id-$run_attempt/evidence" --public-key /run/private/evidence-public.pem >/dev/null \
  || die "restore_dqa_evidence_verify_failed"

docker run -d --name "$redis_container" --network "$network" --network-alias redis \
  --user 999:999 --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:uid=999,gid=999,mode=0700 -v "$redis_secret:/run/private:ro" \
  "$redis_image" redis-server /run/private/redis.conf >/dev/null
docker run -d --name "$api_container" --network "$network" \
  --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:uid=1001,gid=1001,mode=0700 -v "$api_secret:/run/private:ro" \
  -v "$api_config:/app/appsettings.Production.json:ro" -v "$geoip:/app/geoip:ro" \
  -e ASPNETCORE_ENVIRONMENT=Production -e AllowedHosts="$public_host;saydin-api" \
  -e ForwardedHeaders__KnownNetworks="$proxy_network" -e ForwardedHeaders__ForwardLimit=1 \
  -e DistributedSecurityLimiter__Enabled=true -e DistributedSecurityLimiter__HmacKeyFile=/run/private/security-limiter-hmac \
  -e InstallationCredentials__SecretFile=/run/private/installation-keyring.json \
  -e PGHOST=restored-db -e PGPORT=5432 -e PGDATABASE="$database" -e PGUSER="$api_login" -e PGSSLMODE=Disable \
  -e SAYDIN_API_DATABASE_PASSWORD_FILE=/run/private/password \
  -e SAYDIN_DEPLOYMENT_ID="$deployment" -e SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256="$system_id" \
  -e SAYDIN_DATABASE_ROLE_PREFIX="$prefix" -e SAYDIN_DATABASE_LOGIN_VERSION=1 \
  -e GeoIp__DatabasePath=/app/geoip/GeoLite2-City.mmdb -e SAYDIN_RELEASE_VERSION="$release_version" \
  -e SAYDIN_SERVICE_VERSION="$service_version" \
  -e SAYDIN_GIT_SHA="$git_sha" "$api_image" >/dev/null
attempt=0
until docker exec "$api_container" curl -fsS -H "Host: $public_host" \
    http://127.0.0.1:8080/health/live >/dev/null 2>&1; do
  attempt=$((attempt + 1)); [ "$attempt" -lt 30 ] || die "restore_api_smoke_failed"; sleep 2
done

manifest_sha=$(sha256sum "$manifest" | cut -d' ' -f1)
python3 - "$evidence_dir/restore-receipt.json" "$evidence_dir/wal-recovery-evidence.json" \
  "$run_id" "$run_attempt" "$target_time" "$last_replayed_transaction_at" "$manifest_sha" <<'PY'
import datetime, json, sys
path, wal_path, run_id, run_attempt, target, transaction, digest = sys.argv[1:]
parse=lambda value: datetime.datetime.strptime(value,"%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
wal=json.load(open(wal_path,encoding="utf-8"))
assert set(wal)=={"walSegment","walSegmentSourceAt","walCoverageObservedAt","walReceiverCaughtUpAt","walServerLsn","walServerHighwaterSegment","walServerPreviousSegment","walSnapshotReceivedAt","guaranteedRecoveryPointAt","walEvidenceEvaluatedAt","currentRecoveryPointAgeSeconds"}
transaction_lag=max(0,int((parse(target)-parse(transaction)).total_seconds())) if transaction else None
completed=datetime.datetime.now(datetime.timezone.utc)
value = {"schemaVersion":2,"runId":run_id,"runAttempt":run_attempt,
         "targetTime":target,"manifestSha256":digest,**wal,
         "lastReplayedTransactionAt":transaction or None,
         "lastReplayedTransactionLagSeconds":transaction_lag,
         "recoveryTargetReached":True,
         "completedAt":completed.isoformat().replace("+00:00","Z"),
         "roleBootstrap":"passed","migrationTrust":"passed","dqa":"passed","apiSmoke":"passed"}
open(path,"w",encoding="utf-8").write(json.dumps(value,sort_keys=True,separators=(",",":"))+"\n")
PY
