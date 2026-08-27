#!/usr/bin/env bash
set -euo pipefail

readonly timescale_image='timescale/timescaledb@sha256:3adf01543c37b5b88d3c4998338e0f7f21cb3cdd02bbddea08b09bf60e2289b7'
readonly sdk_image='mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c'
run_id="$(openssl rand -hex 16)"
readonly run_id
readonly base="saydin_repair_gate_${run_id}"
readonly network="${base}_net"
readonly pg_name="${base}_pg"
readonly db_host=postgres
readonly bootstrap_volume="${base}_bootstrap_secrets"
readonly test_volume="${base}_test_secrets"
readonly pg_volume="${base}_pgdata"
readonly migrator_image="${base}_migrator"
readonly database="saydin_data_repair_test_${run_id}"
readonly deployment="ci-${run_id:0:8}"
readonly nuget_volume=saydin-nuget-cache
expected_migration_count="$(find infrastructure/postgres/migrations -maxdepth 1 -type f \
  \( -name '*.sql' -o -name '*.sh' \) -print | wc -l | tr -d '[:space:]')"
readonly expected_migration_count
[[ "$expected_migration_count" =~ ^[1-9][0-9]*$ ]]
[[ -z "${TEST_FILTER:-}" ]] || {
  echo 'data_repair_acceptance_failed:test_filter_forbidden' >&2
  exit 64
}
results_directory="$(mktemp -d /tmp/saydin-repair-results.XXXXXX)"
readonly results_directory
readonly result_file="${results_directory}/data-repair-isolated.trx"
backup_valid_until="$(python3 -c \
  'import datetime; print((datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=60)).strftime("%Y-%m-%dT%H:%M:%SZ"))')"
readonly backup_valid_until

[[ -f tests/Saydin.DataRepair.IntegrationTests/Saydin.DataRepair.IntegrationTests.csproj ]]
[[ "$base" =~ ^saydin_repair_gate_[0-9a-f]{32}$ ]]
[[ "$backup_valid_until" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]]
[[ "$results_directory" =~ ^/tmp/saydin-repair-results\.[A-Za-z0-9]+$ ]]
[[ -d "$results_directory" && ! -L "$results_directory" ]]

cleanup() {
  local exit_code=$?
  set +e
  printf 'CLEANUP targets=%s,%s,%s,%s,%s\n' \
    "$pg_name" "$network" "$bootstrap_volume" "$test_volume" "$pg_volume"
  docker rm -f "$pg_name" >/dev/null 2>&1
  docker network rm "$network" >/dev/null 2>&1
  docker volume rm "$bootstrap_volume" "$test_volume" "$pg_volume" >/dev/null 2>&1
  docker image rm "$migrator_image" >/dev/null 2>&1
  if [[ "$results_directory" =~ ^/tmp/saydin-repair-results\.[A-Za-z0-9]+$ \
        && -d "$results_directory" && ! -L "$results_directory" ]]; then
    if [[ ! -e "$result_file" || -f "$result_file" && ! -L "$result_file" ]]; then
      rm -f -- "$result_file"
      rmdir -- "$results_directory"
    fi
  fi
  printf 'CLEANUP complete rc=%s\n' "$exit_code"
  exit "$exit_code"
}
trap cleanup EXIT INT TERM

printf 'STAGE test-build-start\n'
docker run --rm -v "$PWD:/repo" -v "$nuget_volume:/root/.nuget/packages" -w /repo \
  "$sdk_image" dotnet restore \
  tests/Saydin.DataRepair.IntegrationTests/Saydin.DataRepair.IntegrationTests.csproj \
  -p:RestoreLockedMode=true
docker run --rm --network none -v "$PWD:/repo" \
  -v "$nuget_volume:/root/.nuget/packages" -w /repo \
  "$sdk_image" dotnet build \
  tests/Saydin.DataRepair.IntegrationTests/Saydin.DataRepair.IntegrationTests.csproj \
  --no-restore -c Release
printf 'STAGE test-build-complete\n'

printf 'STAGE identifiers run_id=%s database=%s\n' "$run_id" "$database"
docker network create "$network" >/dev/null
docker volume create "$bootstrap_volume" >/dev/null
docker volume create "$test_volume" >/dev/null
docker volume create "$pg_volume" >/dev/null

postgres_password="$(openssl rand -hex 32)"
printf '%s' "$postgres_password" | docker run --rm -i \
  -v "$bootstrap_volume:/run/secrets" --entrypoint sh "$timescale_image" -ec \
  'umask 077; chmod 0700 /run/secrets; chown 1001:1001 /run/secrets;
   cat >/run/secrets/postgres-password; chown 1001:1001 /run/secrets/postgres-password;
   chmod 0400 /run/secrets/postgres-password'
printf '%s' "$postgres_password" | docker run --rm -i \
  -v "$test_volume:/run/secrets" --entrypoint sh "$timescale_image" -ec \
  'umask 077; chmod 0700 /run/secrets; chown 0:0 /run/secrets;
   cat >/run/secrets/postgres-password; chown 0:0 /run/secrets/postgres-password;
   chmod 0400 /run/secrets/postgres-password'

docker run -d --name "$pg_name" --network "$network" --network-alias "$db_host" \
  -e POSTGRES_DB="$database" -e POSTGRES_USER=saydin_ci \
  -e POSTGRES_PASSWORD_FILE=/run/bootstrap-secrets/postgres-password \
  -v "$bootstrap_volume:/run/bootstrap-secrets:ro" \
  -v "$pg_volume:/var/lib/postgresql/data" "$timescale_image" >/dev/null
ready_count=0
for attempt in $(seq 1 120); do
  if docker exec "$pg_name" pg_isready -U saydin_ci -d "$database" >/dev/null 2>&1 \
     && docker exec "$pg_name" psql -X -U saydin_ci -d "$database" -tAc 'SELECT 1' \
        >/dev/null 2>&1; then
    ready_count=$((ready_count + 1))
    [[ "$ready_count" -lt 3 ]] || break
  else
    ready_count=0
  fi
  [[ "$attempt" -lt 120 ]] || exit 91
  sleep 1
done

system_identifier="$(docker exec "$pg_name" psql -X -U saydin_ci -d "$database" \
  -tAc 'SELECT system_identifier::text FROM pg_catalog.pg_control_system()' | tr -d '[:space:]')"
[[ "$system_identifier" =~ ^[0-9]+$ ]]
system_hash="$(printf '%s' "$system_identifier" | shasum -a 256 | cut -d' ' -f1)"
suffix="$(printf '%s\0%s\0%s' "$system_hash" "$database" "$deployment" \
  | shasum -a 256 | cut -c1-24)"
prefix="saydin_ci__${suffix}"
[[ "$prefix" =~ ^saydin_[a-z][a-z0-9_]{2}_[0-9a-f]{24}$ ]]

admin_connection="Host=${db_host};Port=5432;Database=${database};Username=saydin_ci;Password=${postgres_password};SSL Mode=Disable"
printf '%s' "$admin_connection" | docker run --rm -i \
  -v "$bootstrap_volume:/run/secrets" --entrypoint sh "$timescale_image" -ec \
  'cat >/run/secrets/admin; chown 1001:1001 /run/secrets/admin; chmod 0400 /run/secrets/admin'
printf '%s' "$admin_connection" | docker run --rm -i \
  -v "$test_volume:/run/secrets" --entrypoint sh "$timescale_image" -ec \
  'cat >/run/secrets/admin; chown 0:0 /run/secrets/admin; chmod 0400 /run/secrets/admin'
for purpose in migrator api ingestion calendar_importer exporter audit backup; do
  password="$(openssl rand -hex 32)"
  printf '%s' "$password" | docker run --rm -i \
    -v "$bootstrap_volume:/run/secrets" --entrypoint sh "$timescale_image" -ec \
    "cat >/run/secrets/${purpose}-v1; chown 1001:1001 /run/secrets/${purpose}-v1; chmod 0400 /run/secrets/${purpose}-v1"
  if [[ "$purpose" == ingestion || "$purpose" == audit ]]; then
    printf '%s' "$password" | docker run --rm -i \
      -v "$test_volume:/run/secrets" --entrypoint sh "$timescale_image" -ec \
      "cat >/run/secrets/${purpose}-v1; chown 0:0 /run/secrets/${purpose}-v1; chmod 0400 /run/secrets/${purpose}-v1"
  fi
done
unset postgres_password admin_connection password

printf 'STAGE postgres-ready system_hash=%s prefix=%s\n' "$system_hash" "$prefix"
docker build --quiet -f infrastructure/postgres/Dockerfile.migrator \
  -t "$migrator_image" . >/dev/null
printf 'STAGE image-ready\n'

bootstrap=(ensure --admin-connection-file /run/saydin-secrets/admin
  --deployment-id "$deployment" --target-database "$database"
  --system-identifier-sha256 "$system_hash" --role-prefix "$prefix"
  --timescaledb-version 2.16.1 --uuid-ossp-version 1.1
  --backup-v1-valid-until "$backup_valid_until"
  --migrator-password-file /run/saydin-secrets/migrator-v1
  --api-password-file /run/saydin-secrets/api-v1
  --ingestion-password-file /run/saydin-secrets/ingestion-v1
  --calendar-importer-password-file /run/saydin-secrets/calendar_importer-v1
  --exporter-password-file /run/saydin-secrets/exporter-v1
  --audit-password-file /run/saydin-secrets/audit-v1
  --backup-password-file /run/saydin-secrets/backup-v1)
pre="$(docker run --rm --network "$network" \
  -v "$bootstrap_volume:/run/saydin-secrets:ro" --entrypoint dotnet \
  "$migrator_image" Saydin.DatabaseRoleBootstrap.dll "${bootstrap[@]}")"
grep -q 'backup_postbootstrap_required=true' <<<"$pre"
printf 'STAGE pre-bootstrap=true\n'

run_migrator() {
  docker run --rm --network "$network" \
    -v "$bootstrap_volume:/run/saydin-secrets:ro" \
    -e PGHOST="$db_host" -e PGPORT=5432 -e PGDATABASE="$database" \
    -e PGUSER="${prefix}_migrator_login_v1" \
    -e SAYDIN_MIGRATOR_PASSWORD_FILE=/run/saydin-secrets/migrator-v1 \
    -e SAYDIN_DEPLOYMENT_ID="$deployment" \
    -e SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256="$system_hash" \
    -e SAYDIN_DATABASE_ROLE_PREFIX="$prefix" \
    -e SAYDIN_TIMESCALEDB_VERSION=2.16.1 -e SAYDIN_UUID_OSSP_VERSION=1.1 \
    -e SAYDIN_MIGRATOR_LOGIN_VERSION=1 -e SAYDIN_MIGRATOR_LOCK_TIMEOUT_SECONDS=20 \
    -e SAYDIN_MIGRATOR_COMMAND_TIMEOUT_SECONDS=300 \
    -e SAYDIN_BACKUP_V1_VALID_UNTIL="$backup_valid_until" \
    "$migrator_image" "$@"
}
migrate="$(run_migrator)"
grep -q "applied=${expected_migration_count}" <<<"$migrate"
tail -1 <<<"$migrate"

network_cidr="$(docker network inspect --format '{{(index .IPAM.Config 0).Subnet}}' "$network")"
hba_path="$(docker exec "$pg_name" psql -X -U saydin_ci -d "$database" \
  -tAc 'SHOW hba_file' | tr -d '[:space:]')"
[[ "$hba_path" == /*/pg_hba.conf ]]
docker exec -i --user 70:70 "$pg_name" python3 - install \
  --hba "$hba_path" --cidr "$network_cidr" --role-prefix "$prefix" \
  --fixture-cleartext < infrastructure/backup/manage_backup_hba.py
docker exec "$pg_name" psql -X -U saydin_ci -d "$database" \
  -tAc 'SELECT pg_reload_conf()' | grep -q t
printf 'STAGE hba-ready\n'

post="$(docker run --rm --network "$network" \
  -v "$bootstrap_volume:/run/saydin-secrets:ro" --entrypoint dotnet \
  "$migrator_image" Saydin.DatabaseRoleBootstrap.dll "${bootstrap[@]}")"
grep -q 'backup_postbootstrap_required=false' <<<"$post"
verify="$(run_migrator --verify-only)"
grep -q "already_applied=${expected_migration_count}" <<<"$verify"
tail -1 <<<"$verify"

test_command=(dotnet test tests/Saydin.DataRepair.IntegrationTests/Saydin.DataRepair.IntegrationTests.csproj
  --no-build --no-restore -c Release --logger 'console;verbosity=normal'
  --logger 'trx;LogFileName=data-repair-isolated.trx' --results-directory /results)
printf 'STAGE realpg-tests-start\n'
docker run --rm --network "$network" -v "$PWD:/repo" \
  -v "$results_directory:/results" \
  -v "$test_volume:/run/test-secrets:ro" \
  -v "$nuget_volume:/root/.nuget/packages" -w /repo \
  -e SAYDIN_REPAIR_TEST_REQUIRED=true -e SAYDIN_REPAIR_TEST_RUN_ID="$run_id" \
  -e SAYDIN_REPAIR_TEST_EXPECTED_HOST="$db_host" \
  -e SAYDIN_REPAIR_TEST_ADMIN_CONNECTION_FILE=/run/test-secrets/admin \
  -e SAYDIN_REPAIR_TEST_DEPLOYMENT_ID="$deployment" \
  -e SAYDIN_REPAIR_TEST_SYSTEM_IDENTIFIER_SHA256="$system_hash" \
  -e SAYDIN_REPAIR_TEST_ROLE_PREFIX="$prefix" \
  -e SAYDIN_REPAIR_TEST_INGESTION_LOGIN="${prefix}_ingestion_login_v1" \
  -e SAYDIN_REPAIR_TEST_INGESTION_PASSWORD_FILE=/run/test-secrets/ingestion-v1 \
  -e SAYDIN_REPAIR_TEST_AUDIT_LOGIN="${prefix}_audit_login_v1" \
  -e SAYDIN_REPAIR_TEST_AUDIT_PASSWORD_FILE=/run/test-secrets/audit-v1 \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  "$sdk_image" "${test_command[@]}"
python3 .github/scripts/verify-integration-trx.py "$result_file" --minimum-executed 32
printf 'STAGE realpg-tests-complete\n'
