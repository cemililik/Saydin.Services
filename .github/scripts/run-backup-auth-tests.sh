#!/usr/bin/env bash
# Required physical-protocol acceptance for the managed backup login. The
# mounted directory contains one pgpass file and no control-plane credential.
set -euo pipefail
export LC_ALL=C

die() { printf '%s\n' "$1" >&2; exit "${2:-1}"; }

for variable in PGHOST PGPORT PGUSER PGPASSFILE SAYDIN_DATABASE_ROLE_PREFIX \
  SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256 SAYDIN_TARGET_DATABASE SAYDIN_CI_RUN_ID; do
  [[ -n "${!variable:-}" ]] || die "backup_auth_config_missing:$variable" 78
done
[[ "$PGUSER" == "${SAYDIN_DATABASE_ROLE_PREFIX}_backup_login_v1" ]] || \
  die "backup_auth_login_mismatch" 78
[[ "$SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" =~ ^[0-9a-f]{64}$ ]] || \
  die "backup_auth_system_hash_invalid" 78
[[ "$SAYDIN_CI_RUN_ID" =~ ^[0-9a-f]{32}$ ]] || die "backup_auth_run_id_invalid" 78
[[ -f "$PGPASSFILE" && ! -L "$PGPASSFILE" ]] || die "backup_auth_pgpass_invalid" 78
[[ "$(stat -c '%u:%g:%a:%h' "$PGPASSFILE")" == "0:0:400:1" ]] || \
  die "backup_auth_pgpass_permissions_invalid" 78
[[ "$(find "$(dirname "$PGPASSFILE")" -mindepth 1 -maxdepth 1 -printf '%f\n')" == ".pgpass" ]] || \
  die "backup_auth_secret_scope_invalid" 78

target=/work/base
slot="${SAYDIN_DATABASE_ROLE_PREFIX}_backup_ci"
log=/tmp/receivewal.log
receiver=
cleanup() {
  status=$?
  trap - EXIT HUP INT TERM
  if [[ -n "$receiver" ]]; then
    kill "$receiver" >/dev/null 2>&1 || true
    wait "$receiver" >/dev/null 2>&1 || true
  fi
  rm -rf "$target" "$log"
  exit "$status"
}
trap cleanup EXIT HUP INT TERM

mkdir -m 0700 "$target"
pg_receivewal --host="$PGHOST" --port="$PGPORT" --username="$PGUSER" \
  --slot="$slot" --create-slot --if-not-exists --no-password >/dev/null
timeout --signal=TERM 30 pg_receivewal --directory=/work --host="$PGHOST" \
  --port="$PGPORT" --username="$PGUSER" --slot="$slot" --synchronous --no-password \
  >"$log" 2>&1 &
receiver=$!
sleep 1
kill -0 "$receiver" || die "backup_auth_receivewal_failed"

# The managed replication login has CONNECTION LIMIT 2. A persistent WAL
# receiver consumes one connection, so the base backup must use the one-session
# fetch mode; stream mode would silently require a third connection.
pg_basebackup --host="$PGHOST" --port="$PGPORT" --username="$PGUSER" \
  --pgdata="$target" --format=plain --wal-method=fetch --checkpoint=fast \
  --manifest-checksums=SHA256 --no-password >/dev/null
kill -0 "$receiver" || die "backup_auth_receivewal_died_during_basebackup"
pg_verifybackup "$target" >/dev/null
system_identifier="$(pg_controldata "$target" | sed -n 's/^Database system identifier: *//p')"
[[ "$system_identifier" =~ ^[0-9]+$ ]] || die "backup_auth_system_identifier_missing"
[[ "$(printf '%s' "$system_identifier" | sha256sum | cut -d' ' -f1)" == \
    "$SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" ]] || die "backup_auth_wrong_cluster"

! grep -Eqi 'fatal|password authentication failed|no pg_hba.conf entry' "$log" || \
  die "backup_auth_receivewal_rejected"
kill "$receiver" >/dev/null 2>&1 || true
wait "$receiver" >/dev/null 2>&1 || true
receiver=
pg_receivewal --host="$PGHOST" --port="$PGPORT" --username="$PGUSER" \
  --slot="$slot" --drop-slot --no-password >/dev/null

for database in postgres "$SAYDIN_TARGET_DATABASE" template0 template1; do
  if psql -X -A -t --no-password --host="$PGHOST" --port="$PGPORT" \
      --username="$PGUSER" --dbname="$database" -c 'SELECT 1' >"$log" 2>&1; then
    die "backup_auth_sql_access_allowed"
  fi
  grep -q 'pg_hba.conf rejects connection' "$log" || die "backup_auth_sql_rejection_not_hba"
done

printf 'backup_auth_acceptance_passed:basebackup,receivewal,sql-deny\n'
