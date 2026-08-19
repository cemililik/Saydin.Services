#!/bin/sh
# Destructive operations are confined to exact disposable Docker resources and an
# openat-validated restore target. Evidence signing uses OCI instance principal KMS.
set -eu
umask 077

die() { printf '%s\n' "$1" >&2; exit "${2:-70}"; }
[ "$#" -eq 6 ] || die "restore_drill_usage" 64
run_id=$1
target_time=$2
manifest=$3
deployment_env=$4
contract_env=$5
evidence_dir=$6
case "$run_id" in ""|*[!0-9]*) die "restore_run_id_invalid" 64 ;; esac
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

python3 infrastructure/release/release_manifest.py verify --manifest "$manifest" >/dev/null
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

prefix_name="saydin-restore-$run_id"
network="$prefix_name-net"
egress_network="$prefix_name-egress"
volume="$prefix_name-data"
database_container="$prefix_name-db"
redis_container="$prefix_name-redis"
api_container="$prefix_name-api"
dqa_container="$prefix_name-dqa"
case "$prefix_name" in saydin-restore-*) ;; *) die "restore_resource_guard_failed" 78 ;; esac

cleanup() {
  docker rm -f "$dqa_container" "$api_container" "$redis_container" "$database_container" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  docker network rm "$egress_network" >/dev/null 2>&1 || true
}
trap cleanup EXIT HUP INT TERM

docker network create --internal "$network" >/dev/null
docker network create "$egress_network" >/dev/null
docker volume create "$volume" >/dev/null
docker run --rm --user 0:0 --read-only --cap-drop ALL --security-opt no-new-privileges \
  -v "$volume:/restore-drill" --entrypoint /bin/sh "$backup_image" \
  -c 'chown 1001:1001 /restore-drill && chmod 0700 /restore-drill'

docker run --rm --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
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
docker run --rm --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
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

recovered_at=$(docker run --rm --network "$network" --user 1001:1001 --read-only --cap-drop ALL \
  --security-opt no-new-privileges -v "$bootstrap_secret:/run/private:ro" \
  -e PGPASSFILE=/run/private/admin-pgpass --entrypoint psql "$backup_image" \
  -X -A -t -v ON_ERROR_STOP=1 -h restored-db -p 5432 -U saydin_admin -d "$database" \
  -c "SELECT to_char(pg_last_xact_replay_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"');")
[ -n "$recovered_at" ] || die "restore_recovery_timestamp_missing"
lag_seconds=$(python3 - "$target_time" "$recovered_at" <<'PY'
import datetime, sys
parse=lambda value: datetime.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
lag=int((parse(sys.argv[1])-parse(sys.argv[2])).total_seconds())
if lag < 0 or lag > 900: raise SystemExit("restore_rpo_exceeded")
print(lag)
PY
) || die "restore_rpo_exceeded"

docker run --rm --network "$network" --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  -v "$bootstrap_secret:/run/private:ro" --entrypoint dotnet "$control_image" \
  Saydin.DatabaseRoleBootstrap.dll verify --admin-connection-file /run/private/admin-connection \
  --deployment-id "$deployment" --target-database "$database" --system-identifier-sha256 "$system_id" \
  --role-prefix "$prefix" --timescaledb-version 2.16.1 --uuid-ossp-version 1.1 \
  --backup-v1-valid-until "$backup_v1_valid_until" >/dev/null

docker run --rm --network "$network" --user 1001:1001 --read-only --cap-drop ALL --security-opt no-new-privileges \
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
  --hmac-key-file /run/private/evidence-hmac --output /run/output/evidence >/dev/null
docker network connect "$egress_network" "$dqa_container"
docker start -a "$dqa_container" >/dev/null || die "restore_dqa_failed"
docker rm "$dqa_container" >/dev/null
docker run --rm --network none --user 1001:1001 --read-only --cap-drop ALL \
  --security-opt no-new-privileges -v "$audit_secret:/run/private:ro" \
  -v "$audit_output:/run/output:ro" "$dqa_image" verify-evidence \
  --bundle /run/output/evidence --public-key /run/private/evidence-public.pem >/dev/null \
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
python3 - "$evidence_dir/restore-receipt.json" "$run_id" "$target_time" "$recovered_at" "$lag_seconds" "$manifest_sha" <<'PY'
import datetime, json, sys
path, run_id, target, recovered, lag, digest = sys.argv[1:]
value = {"schemaVersion":1,"runId":run_id,"targetTime":target,"manifestSha256":digest,
         "recoveredAt":recovered,"recoveryLagSeconds":int(lag),
         "completedAt":datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00","Z"),
         "roleBootstrap":"passed","migrationTrust":"passed","dqa":"passed","apiSmoke":"passed"}
open(path,"w",encoding="utf-8").write(json.dumps(value,sort_keys=True,separators=(",",":"))+"\n")
PY
printf '%s\n' restore_drill_passed
