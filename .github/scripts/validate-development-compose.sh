#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
temporary="$(mktemp -d)"
cleanup() {
  rm -f -- "${temporary:?}/a.json" "${temporary:?}/b.json"
  rmdir -- "${temporary:?}"
}
trap cleanup EXIT INT TERM

render() {
  local project="$1"
  local heartbeat="$2"
  local api_port="$3"
  local output="$4"
  env \
    SAYDIN_DEPLOYMENT_ID=dev-a \
    SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
    SAYDIN_DATABASE_ROLE_PREFIX=saydin_dev_aaaaaaaaaaaaaaaaaaaaaaaa \
    SAYDIN_MIGRATOR_LOGIN=saydin_dev_aaaaaaaaaaaaaaaaaaaaaaaa_migrator_login_v1 \
    SAYDIN_API_LOGIN=saydin_dev_aaaaaaaaaaaaaaaaaaaaaaaa_api_login_v1 \
    SAYDIN_INGESTION_LOGIN=saydin_dev_aaaaaaaaaaaaaaaaaaaaaaaa_ingestion_login_v1 \
    SAYDIN_CALENDAR_IMPORTER_LOGIN=saydin_dev_aaaaaaaaaaaaaaaaaaaaaaaa_calendar_importer_login_v1 \
    SAYDIN_EXPORTER_LOGIN=saydin_dev_aaaaaaaaaaaaaaaaaaaaaaaa_exporter_login_v1 \
    SAYDIN_AUDIT_LOGIN=saydin_dev_aaaaaaaaaaaaaaaaaaaaaaaa_audit_login_v1 \
    PGADMIN_PASSWORD=config-validation-only \
    REDIS_PASSWORD=config-validation-only \
    SAYDIN_INGESTION_HEARTBEAT_PATH="$heartbeat" \
    SAYDIN_API_PORT="$api_port" \
    docker compose --project-name "$project" --file "$repo_root/docker-compose.yml" \
      --profile '*' config --format json > "$output"
}

render saydin_dev_a /tmp/saydin-ingestion-healthy 5080 "$temporary/a.json"
render saydin_dev_b /tmp/saydin-ingestion-alternate 15080 "$temporary/b.json"
python3 "$repo_root/.github/scripts/validate-development-compose.py" "$temporary/a.json" "$temporary/b.json"
