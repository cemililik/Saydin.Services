#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "$script_dir/../.." && pwd -P)"
metadata_file="$repo_root/.env.database-runtime"

cd "$repo_root"

bootstrap_compose() {
  env \
    SAYDIN_DEPLOYMENT_ID="${SAYDIN_DEPLOYMENT_ID:-dev-a}" \
    SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256=0000000000000000000000000000000000000000000000000000000000000000 \
    SAYDIN_DATABASE_ROLE_PREFIX=saydin_dev_000000000000000000000000 \
    SAYDIN_MIGRATOR_LOGIN=bootstrap-not-started \
    SAYDIN_API_LOGIN=bootstrap-not-started \
    SAYDIN_INGESTION_LOGIN=bootstrap-not-started \
    SAYDIN_CALENDAR_IMPORTER_LOGIN=bootstrap-not-started \
    SAYDIN_EXPORTER_LOGIN=bootstrap-not-started \
    PGADMIN_PASSWORD=bootstrap-service-not-started \
    REDIS_PASSWORD=bootstrap-service-not-started \
    docker compose "$@"
}

if [[ -L "$metadata_file" ]]; then
  printf '%s\n' 'bootstrap rejected: runtime metadata path is a symbolic link' >&2
  exit 2
fi

# Only Docker volumes hold secret bytes. The host artifact created below is
# non-secret target identity/role metadata and is safe to pass as --env-file.
bootstrap_compose up --detach --build --wait postgres

metadata="$(bootstrap_compose run --rm --no-deps database-identity)"
expected_keys=(
  SAYDIN_DEPLOYMENT_ID
  SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256
  SAYDIN_DATABASE_ROLE_PREFIX
  SAYDIN_MIGRATOR_LOGIN
  SAYDIN_API_LOGIN
  SAYDIN_INGESTION_LOGIN
  SAYDIN_CALENDAR_IMPORTER_LOGIN
  SAYDIN_EXPORTER_LOGIN
  SAYDIN_AUDIT_LOGIN
)

[[ "$(printf '%s\n' "$metadata" | wc -l | tr -d ' ')" == "${#expected_keys[@]}" ]] || {
  printf '%s\n' 'bootstrap rejected: database identity output shape invalid' >&2
  exit 3
}
for key in "${expected_keys[@]}"; do
  [[ "$(printf '%s\n' "$metadata" | grep -c "^${key}=")" == 1 ]] || {
    printf 'bootstrap rejected: missing or duplicate metadata key %s\n' "$key" >&2
    exit 3
  }
done
if printf '%s\n' "$metadata" | grep -Eqi 'password|secret|url=|connection'; then
  printf '%s\n' 'bootstrap rejected: secret-shaped identity output' >&2
  exit 3
fi
printf '%s\n' "$metadata" | grep -Eq '^SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256=[0-9a-f]{64}$' || exit 3
printf '%s\n' "$metadata" | grep -Eq '^SAYDIN_DATABASE_ROLE_PREFIX=saydin_[a-z][a-z0-9_]{2}_[0-9a-f]{24}$' || exit 3

temporary_file="$(mktemp "$repo_root/.env.database-runtime.XXXXXX")"
cleanup() {
  if [[ -n "${temporary_file:-}" && -f "$temporary_file" ]]; then
    rm -- "$temporary_file"
  fi
}
trap cleanup EXIT INT TERM
chmod 0600 "$temporary_file"
printf '%s\n' "$metadata" > "$temporary_file"
mv -f -- "$temporary_file" "$metadata_file"
temporary_file=''

docker compose --env-file "$metadata_file" config --quiet
printf 'database runtime metadata ready: %s\n' "$metadata_file"
printf '%s\n' 'start with: docker compose --env-file .env.database-runtime up --build'
