#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
metadata_file="$repo_root/.env.database-runtime"
expected_migration_count="$(find "$repo_root/infrastructure/postgres/migrations" \
  -maxdepth 1 -type f \( -name '*.sql' -o -name '*.sh' \) -print | wc -l | tr -d '[:space:]')"
[[ "$expected_migration_count" =~ ^[1-9][0-9]*$ ]] || {
  printf '%s\n' 'development_compose_smoke_failed:migration_inventory_invalid' >&2
  exit 78
}

[[ "$(git -C "$repo_root" rev-parse --is-inside-work-tree 2>/dev/null)" == true ]] || {
  printf '%s\n' 'development_compose_smoke_failed:git_worktree_required' >&2
  exit 78
}
if [[ -e "$metadata_file" || -L "$metadata_file" ]]; then
  printf '%s\n' 'development_compose_smoke_failed:runtime_metadata_already_exists' >&2
  exit 78
fi

run_id="$(openssl rand -hex 8)"
run_project="saydin_dev_smoke_${run_id}"
[[ "$run_project" =~ ^saydin_dev_smoke_[0-9a-f]{16}$ ]] || exit 78

runtime_env=(
  env
  COMPOSE_PROJECT_NAME="$run_project"
  COMPOSE_PROGRESS=quiet
  PGADMIN_PASSWORD=development-compose-smoke-only
  REDIS_PASSWORD=development-compose-smoke-only
  SAYDIN_POSTGRES_PORT=0
  SAYDIN_REDIS_PORT=0
  SAYDIN_API_PORT=0
)
placeholder_metadata=(
  SAYDIN_DEPLOYMENT_ID=dev-a
  SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256=0000000000000000000000000000000000000000000000000000000000000000
  SAYDIN_DATABASE_ROLE_PREFIX=saydin_dev_000000000000000000000000
  SAYDIN_MIGRATOR_LOGIN=bootstrap-not-started
  SAYDIN_API_LOGIN=bootstrap-not-started
  SAYDIN_INGESTION_LOGIN=bootstrap-not-started
  SAYDIN_CALENDAR_IMPORTER_LOGIN=bootstrap-not-started
  SAYDIN_EXPORTER_LOGIN=bootstrap-not-started
  SAYDIN_AUDIT_LOGIN=bootstrap-not-started
  SAYDIN_BACKUP_V1_VALID_UNTIL=2099-01-01T00:00:00Z
)

runtime_compose() {
  "${runtime_env[@]}" docker compose --env-file "$metadata_file" "$@"
}

cleanup() {
  test_exit=$?
  cleanup_exit=0
  trap - EXIT INT TERM

  if [[ ! "$run_project" =~ ^saydin_dev_smoke_[0-9a-f]{16}$ ]]; then
    printf '%s\n' 'development_compose_smoke_cleanup_failed:project_guard' >&2
    exit 70
  fi

  printf 'development_compose_smoke_cleanup_targets:project=%s\n' "$run_project"
  docker ps -a --filter "label=com.docker.compose.project=$run_project" \
    --format 'container={{.Names}}' || cleanup_exit=70
  docker volume ls --filter "label=com.docker.compose.project=$run_project" \
    --format 'volume={{.Name}}' || cleanup_exit=70
  docker network ls --filter "label=com.docker.compose.project=$run_project" \
    --format 'network={{.Name}}' || cleanup_exit=70

  if [[ -f "$metadata_file" && ! -L "$metadata_file" ]]; then
    runtime_compose down --volumes --remove-orphans || cleanup_exit=70
  else
    "${runtime_env[@]}" "${placeholder_metadata[@]}" \
      docker compose down --volumes --remove-orphans || cleanup_exit=70
  fi

  for image_ref in \
    "$run_project-database-migrator:latest" \
    "$run_project-database-role-bootstrap:latest" \
    "$run_project-database-role-bootstrap-post-migration:latest" \
    "$run_project-saydin-api:latest"; do
    [[ "$image_ref" == "$run_project-"*":latest" ]] || exit 70
    if docker image inspect "$image_ref" >/dev/null 2>&1; then
      printf 'image=%s\n' "$image_ref"
      docker image rm "$image_ref" >/dev/null || cleanup_exit=70
    fi
  done

  if [[ -f "$metadata_file" && ! -L "$metadata_file" ]]; then
    file_mode="$(stat -c '%a' "$metadata_file" 2>/dev/null || stat -f '%Lp' "$metadata_file")"
    file_uid="$(stat -c '%u' "$metadata_file" 2>/dev/null || stat -f '%u' "$metadata_file")"
    if [[ "$file_mode" == 600 && "$file_uid" == "$(id -u)" ]]; then
      rm -- "$metadata_file"
    else
      printf 'development_compose_smoke_cleanup_failed:metadata_guard:mode=%s:uid=%s\n' \
        "$file_mode" "$file_uid" >&2
      cleanup_exit=70
    fi
  fi

  residual="$(
    docker ps -a --filter "label=com.docker.compose.project=$run_project" -q | wc -l | tr -d ' '
  ):$(
    docker volume ls --filter "label=com.docker.compose.project=$run_project" -q | wc -l | tr -d ' '
  ):$(
    docker network ls --filter "label=com.docker.compose.project=$run_project" -q | wc -l | tr -d ' '
  ):$(
    docker image ls --format '{{.Repository}}:{{.Tag}}' \
      | awk -v prefix="^${run_project}-" '$0 ~ prefix {count++} END {print count+0}'
  )"
  printf 'development_compose_smoke_cleanup:residual=%s\n' "$residual"
  [[ "$residual" == 0:0:0:0 ]] || cleanup_exit=70

  if [[ "$cleanup_exit" -ne 0 ]]; then
    exit "$cleanup_exit"
  fi
  exit "$test_exit"
}

trap cleanup EXIT INT TERM
cd "$repo_root"
printf 'development_compose_smoke_start:project=%s\n' "$run_project"
"${runtime_env[@]}" ./infrastructure/secrets/bootstrap-dev-database.sh

if ! runtime_compose up --detach --build saydin-api postgres-exporter redis-exporter; then
  runtime_compose logs database-backup-hba database-role-bootstrap \
    database-migrator database-role-bootstrap-post-migration || true
  exit 1
fi

ready=0
for _attempt in $(seq 1 90); do
  pre_id="$(runtime_compose ps -aq database-role-bootstrap)"
  migrator_id="$(runtime_compose ps -aq database-migrator)"
  hba_id="$(runtime_compose ps -aq database-backup-hba)"
  post_id="$(runtime_compose ps -aq database-role-bootstrap-post-migration)"
  api_id="$(runtime_compose ps -q saydin-api)"
  postgres_id="$(runtime_compose ps -q postgres)"
  redis_id="$(runtime_compose ps -q redis)"
  postgres_exporter_id="$(runtime_compose ps -q postgres-exporter)"
  redis_exporter_id="$(runtime_compose ps -q redis-exporter)"

  if [[ -n "$pre_id" && -n "$migrator_id" && -n "$hba_id" && -n "$post_id" ]]; then
    pre_state="$(docker inspect -f '{{.State.Status}}:{{.State.ExitCode}}' "$pre_id")"
    migrator_state="$(docker inspect -f '{{.State.Status}}:{{.State.ExitCode}}' "$migrator_id")"
    hba_state="$(docker inspect -f '{{.State.Status}}:{{.State.ExitCode}}' "$hba_id")"
    post_state="$(docker inspect -f '{{.State.Status}}:{{.State.ExitCode}}' "$post_id")"
    if [[ "$pre_state" == exited:* && "$pre_state" != exited:0 ]] \
        || [[ "$migrator_state" == exited:* && "$migrator_state" != exited:0 ]] \
        || [[ "$hba_state" == exited:* && "$hba_state" != exited:0 ]] \
        || [[ "$post_state" == exited:* && "$post_state" != exited:0 ]]; then
      runtime_compose logs database-backup-hba database-role-bootstrap \
        database-migrator database-role-bootstrap-post-migration
      exit 1
    fi

    if [[ "$pre_state:$migrator_state:$hba_state:$post_state" \
        == exited:0:exited:0:exited:0:exited:0 ]] \
        && [[ -n "$api_id" && -n "$postgres_id" && -n "$redis_id" ]] \
        && [[ -n "$postgres_exporter_id" && -n "$redis_exporter_id" ]]; then
      api_health="$(docker inspect -f '{{.State.Health.Status}}' "$api_id")"
      postgres_health="$(docker inspect -f '{{.State.Health.Status}}' "$postgres_id")"
      redis_health="$(docker inspect -f '{{.State.Health.Status}}' "$redis_id")"
      postgres_exporter_health="$(docker inspect -f '{{.State.Health.Status}}' "$postgres_exporter_id")"
      redis_exporter_state="$(docker inspect -f '{{.State.Status}}' "$redis_exporter_id")"
      if [[ "$api_health:$postgres_health:$redis_health:$postgres_exporter_health:$redis_exporter_state" \
          == healthy:healthy:healthy:healthy:running ]]; then
        ready=1
        break
      fi
    fi
  fi
  sleep 2
done

if [[ "$ready" -ne 1 ]]; then
  runtime_compose ps --all
  runtime_compose logs database-backup-hba database-role-bootstrap \
    database-migrator database-role-bootstrap-post-migration saydin-api postgres-exporter
  exit 1
fi

runtime_compose run --rm --no-deps database-migrator --verify-only \
  | grep -q "applied=0; already_applied=${expected_migration_count}; skipped_optional=0; backup_postbootstrap_required=false"
runtime_compose run --rm --no-deps database-role-bootstrap-post-migration \
  | grep -q 'backup_postbootstrap_required=false'
runtime_compose run --rm --no-deps database-migrator --verify-only \
  | grep -q "applied=0; already_applied=${expected_migration_count}; skipped_optional=0; backup_postbootstrap_required=false"

printf 'development_compose_smoke_passed:project=%s\n' "$run_project"
