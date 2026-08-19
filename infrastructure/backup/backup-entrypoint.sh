#!/bin/sh
set -eu
umask 077

die() { printf '%s\n' "$1" >&2; exit "${2:-70}"; }
require() { eval "value=\${$1-}"; [ -n "$value" ] || die "backup_config_missing:$1" 78; }

pgpass=
receiver=
case "${1-}" in
  base-backup) backup_kind=base ;;
  base-backup-loop) backup_kind=base ;;
  wal-stream) backup_kind=wal ;;
  restore) backup_kind=restore ;;
  verify-auth) backup_kind=auth ;;
  *) backup_kind=invalid ;;
esac

private_file() {
  path=$1
  case "$path" in /*) ;; *) die "backup_secret_path_not_absolute" 78 ;; esac
  [ -f "$path" ] && [ ! -L "$path" ] || die "backup_secret_file_invalid" 78
  links=$(stat -c %h "$path" 2>/dev/null || stat -f %l "$path")
  mode=$(stat -c %a "$path" 2>/dev/null || stat -f %Lp "$path")
  owner=$(stat -c %u "$path" 2>/dev/null || stat -f %u "$path")
  [ "$links" = 1 ] || die "backup_secret_link_count_invalid" 78
  [ "$owner" = "$(id -u)" ] || die "backup_secret_owner_invalid" 78
  case "$mode" in 400|600) ;; *) die "backup_secret_mode_invalid" 78 ;; esac
  size=$(wc -c < "$path" | tr -d ' ')
  [ "$size" -ge 16 ] && [ "$size" -le 4096 ] || die "backup_secret_length_invalid" 78
  [ "$(tail -c 1 "$path" | wc -l | tr -d ' ')" = 0 ] || die "backup_secret_newline_invalid" 78
}

configure_repository() {
  for name in RESTIC_REPOSITORY RESTIC_PASSWORD_FILE AWS_WEB_IDENTITY_TOKEN_FILE AWS_ROLE_ARN AWS_REGION SAYDIN_BACKUP_BUCKET SAYDIN_BACKUP_KMS_KEY_ID SAYDIN_DEPLOYMENT_ID; do require "$name"; done
  private_file "$RESTIC_PASSWORD_FILE"
  private_file "$AWS_WEB_IDENTITY_TOKEN_FILE"
  case "$RESTIC_REPOSITORY" in s3:https://*) ;; *) die "backup_off_host_repository_required" 78 ;; esac
  case "$RESTIC_REPOSITORY" in *"@"*|*"?"*|*"#"*) die "backup_repository_credentials_or_query_forbidden" 78 ;; esac
  case "$SAYDIN_BACKUP_BUCKET" in ""|*[!a-z0-9.-]*|.*|*.) die "backup_bucket_invalid" 78 ;; esac
  bucket_length=$(printf %s "$SAYDIN_BACKUP_BUCKET" | wc -c | tr -d ' ')
  [ "$bucket_length" -ge 3 ] && [ "$bucket_length" -le 63 ] || die "backup_bucket_invalid" 78
  case "$SAYDIN_BACKUP_BUCKET" in *..*) die "backup_bucket_invalid" 78 ;; esac
  case "$RESTIC_REPOSITORY" in *"/$SAYDIN_BACKUP_BUCKET/"*) ;; *) die "backup_repository_bucket_mismatch" 78 ;; esac
  case "$SAYDIN_DEPLOYMENT_ID" in ""|*[!a-z0-9-]*|-*|*-) die "backup_deployment_id_invalid" 78 ;; esac
  deployment_length=$(printf %s "$SAYDIN_DEPLOYMENT_ID" | wc -c | tr -d ' ')
  [ "$deployment_length" -ge 3 ] && [ "$deployment_length" -le 63 ] || die "backup_deployment_id_invalid" 78
  [ "${SAYDIN_BACKUP_RPO_MINUTES-}" = 15 ] || die "backup_rpo_policy_mismatch" 78
  [ "${SAYDIN_BACKUP_RTO_MINUTES-}" = 120 ] || die "backup_rto_policy_mismatch" 78
  mkdir -p /tmp/restic-cache
  chmod 0700 /tmp/restic-cache
  export RESTIC_CACHE_DIR=/tmp/restic-cache
  export RESTIC_PASSWORD_FILE
}

configure_database() {
  for name in PGHOST PGPORT PGDATABASE PGUSER SAYDIN_DATABASE_ROLE_PREFIX \
    SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256 SAYDIN_BACKUP_DATABASE_PASSWORD_FILE; do require "$name"; done
  case "$SAYDIN_DATABASE_ROLE_PREFIX" in ""|*[!a-z0-9_]*) die "backup_role_prefix_invalid" 78 ;; esac
  [ "$PGUSER" = "${SAYDIN_DATABASE_ROLE_PREFIX}_backup_login_v1" ] || die "backup_login_identity_invalid" 78
  [ "${#SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256}" -eq 64 ] || \
    die "backup_system_identifier_hash_invalid" 78
  case "$SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" in
    *[!0-9a-f]*) die "backup_system_identifier_hash_invalid" 78 ;;
  esac
  private_file "$SAYDIN_BACKUP_DATABASE_PASSWORD_FILE"
  pgpass=/tmp/.pgpass
  password=$(cat "$SAYDIN_BACKUP_DATABASE_PASSWORD_FILE")
  case "$password" in *:*) die "backup_database_password_character_invalid" 78 ;; esac
  # Physical replication connections do not use the application database name; the
  # role/host/port remain exact and the managed login has no database/table capability.
  printf '%s:%s:*:%s:%s\n' "$PGHOST" "$PGPORT" "$PGUSER" "$password" > "$pgpass"
  unset password
  chmod 0600 "$pgpass"
  export PGPASSFILE=$pgpass
}

ensure_repository() {
  restic snapshots --no-lock >/dev/null 2>&1 || die "backup_repository_not_initialized"
}

write_metric() {
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  [ -d "$metric_dir" ] || return 0
  now=$(date +%s)
  temp="$metric_dir/.saydin_backup_$1.prom.$$"
  printf 'saydin_backup_last_success_timestamp_seconds{kind="%s"} %s\n' "$1" "$now" > "$temp"
  mv "$temp" "$metric_dir/saydin_backup_$1.prom"
}

write_failure_metric() {
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  [ -d "$metric_dir" ] || return 0
  now=$(date +%s)
  temp="$metric_dir/.saydin_backup_failure_$1.prom.$$"
  printf 'saydin_backup_last_failure_timestamp_seconds{kind="%s"} %s\n' "$1" "$now" > "$temp"
  mv "$temp" "$metric_dir/saydin_backup_failure_$1.prom"
}

backup_exit() {
  status=$1
  trap - EXIT HUP INT TERM
  if [ -n "$receiver" ]; then
    kill "$receiver" 2>/dev/null || true
    wait "$receiver" 2>/dev/null || true
  fi
  [ -z "$pgpass" ] || rm -f "$pgpass"
  rm -f /tmp/pg_receivewal.log /tmp/restic-base.json /tmp/restic-wal.json /tmp/restic-base-snapshots.json
  if [ "$status" -ne 0 ]; then
    write_failure_metric "$backup_kind" || true
  fi
  exit "$status"
}

trap 'backup_exit $?' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

base_backup() {
  configure_repository
  configure_database
  ensure_repository
  target=/tmp/basebackup
  rm -rf "$target"
  mkdir -m 0700 "$target"
  pg_basebackup --host="$PGHOST" --port="$PGPORT" --dbname="$PGDATABASE" --username="$PGUSER" \
    --pgdata="$target" --format=plain --wal-method=fetch --checkpoint=fast \
    --manifest-checksums=SHA256 --no-password --verbose >/dev/null
  pg_verifybackup "$target" >/dev/null || die "backup_manifest_verification_failed"
  system_identifier=$(pg_controldata "$target" | sed -n 's/^Database system identifier: *//p')
  case "$system_identifier" in ""|*[!0-9]*) die "backup_system_identifier_missing" ;; esac
  observed_hash=$(printf '%s' "$system_identifier" | sha256sum | cut -d' ' -f1)
  [ "$observed_hash" = "$SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" ] || \
    die "backup_wrong_cluster"
  restic backup "$target" --tag saydin --tag base --tag "kms:$SAYDIN_BACKUP_KMS_KEY_ID" --host "$SAYDIN_DEPLOYMENT_ID" --json >/tmp/restic-base.json
  restic forget --tag base --keep-daily 14 --keep-weekly 8 --keep-monthly 12 --prune >/dev/null
  write_metric base
  rm -rf "$target" /tmp/restic-base.json
}

base_backup_loop() {
  while :; do
    sleep 86400 &
    wait $!
    base_backup
  done
}

wal_stream() {
  configure_repository
  configure_database
  ensure_repository
  spool=/work/wal
  mkdir -p "$spool"
  chmod 0700 "$spool"
  slot="${SAYDIN_DATABASE_ROLE_PREFIX}_backup_slot_v1"
  slot_length=$(printf %s "$slot" | wc -c | tr -d ' ')
  [ "$slot_length" -le 63 ] || die "backup_replication_slot_invalid" 78
  pg_receivewal --host="$PGHOST" --port="$PGPORT" --username="$PGUSER" \
    --no-password --slot="$slot" --create-slot --if-not-exists \
    >/dev/null 2>&1 || die "backup_replication_slot_create_failed"
  pg_receivewal --directory="$spool" --host="$PGHOST" --port="$PGPORT" --username="$PGUSER" \
    --no-password --synchronous --slot="$slot" \
    >/tmp/pg_receivewal.log 2>&1 &
  receiver=$!
  interval=$((15 * 60))
  while kill -0 "$receiver" 2>/dev/null; do
    sleep "$interval" & wait $! || true
    kill -0 "$receiver" 2>/dev/null || break
    restic backup "$spool" --exclude='*.partial' --tag saydin --tag wal --tag "kms:$SAYDIN_BACKUP_KMS_KEY_ID" --host "$SAYDIN_DEPLOYMENT_ID" --json >/tmp/restic-wal.json
    restic forget --tag wal --keep-within 14d >/dev/null
    find "$spool" -type f ! -name '*.partial' -mtime +14 -delete
    write_metric wal
    rm -f /tmp/restic-wal.json
  done
  wait "$receiver" || die "backup_wal_receiver_failed"
}

verify_auth() {
  configure_database
  export LC_ALL=C
  log=/tmp/backup-auth-sql.log
  for database in postgres "$PGDATABASE" template0 template1; do
    if psql -X -A -t --no-password --host="$PGHOST" --port="$PGPORT" \
        --username="$PGUSER" --dbname="$database" -c 'SELECT 1' >"$log" 2>&1; then
      die "backup_sql_access_allowed"
    fi
    grep -q 'pg_hba.conf rejects connection' "$log" || die "backup_sql_rejection_not_hba"
  done
  rm -f "$log"
  printf 'backup_auth_sql_deny_ok\n'
}

restore_snapshot() {
  configure_repository
  require SAYDIN_RESTORE_TARGET
  require SAYDIN_RESTORE_CONFIRM
  require SAYDIN_RESTORE_TARGET_TIME
  [ "$SAYDIN_RESTORE_CONFIRM" = "DISPOSABLE_RESTORE_ONLY" ] || die "restore_confirmation_failed" 78
  guarded_target=$(saydin-validate-restore-target "$SAYDIN_RESTORE_TARGET") \
    || die "restore_target_guard_failed" 78
  [ "$guarded_target" = "$SAYDIN_RESTORE_TARGET" ] || die "restore_target_guard_failed" 78
  restic snapshots --tag base --host "$SAYDIN_DEPLOYMENT_ID" --json > /tmp/restic-base-snapshots.json
  snapshot=$(saydin-select-base-snapshot /tmp/restic-base-snapshots.json "$SAYDIN_RESTORE_TARGET_TIME") \
    || die "restore_base_before_target_missing"
  [ -n "$snapshot" ] || die "restore_base_before_target_missing"
  restic restore "$snapshot" --target "$SAYDIN_RESTORE_TARGET/base" --verify >/dev/null
  restic restore latest --tag wal --host "$SAYDIN_DEPLOYMENT_ID" \
    --target "$SAYDIN_RESTORE_TARGET/wal" --verify >/dev/null
  base=$(find "$SAYDIN_RESTORE_TARGET/base" -name backup_manifest -type f -print -quit)
  [ -n "$base" ] || die "restore_backup_manifest_missing"
  data_dir=$(dirname "$base")
  pg_verifybackup "$data_dir" >/dev/null || die "restore_backup_manifest_invalid"
  canonical=/restore-drill/pgdata
  [ ! -e "$canonical" ] || die "restore_canonical_target_exists"
  mv "$data_dir" "$canonical"
  printf '%s\n' "$canonical"
}

case "${1-}" in
  base-backup) base_backup ;;
  base-backup-loop) base_backup_loop ;;
  wal-stream) wal_stream ;;
  restore) restore_snapshot ;;
  verify-auth) verify_auth ;;
  *) die "backup_command_invalid" 64 ;;
esac
