#!/bin/sh
set -eu
umask 077

die() { printf '%s\n' "$1" >&2; exit "${2:-70}"; }
require() { eval "value=\${$1-}"; [ -n "$value" ] || die "backup_config_missing:$1" 78; }

pgpass=
receiver=
active_child=
base_staging_root=
base_target=
base_lock_held=
restic_retry_lock=15m
physical_probe_lock_held=
spool=
observation_temp=
watermark_temp=
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

run_tracked() {
  "$@" &
  active_child=$!
  if wait "$active_child"; then
    status=0
  else
    status=$?
  fi
  active_child=
  return "$status"
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
  export RESTIC_PASSWORD_FILE
}

configure_cache() {
  cache=$1
  case "$cache" in /*) ;; *) die "backup_cache_path_not_absolute" 78 ;; esac
  if [ -e "$cache" ]; then
    [ -d "$cache" ] && [ ! -L "$cache" ] || die "backup_cache_directory_invalid" 78
  else
    mkdir -m 0700 "$cache" || die "backup_cache_create_failed" 75
  fi
  owner=$(stat -c %u "$cache" 2>/dev/null || stat -f %u "$cache")
  group=$(stat -c %g "$cache" 2>/dev/null || stat -f %g "$cache")
  mode=$(stat -c %a "$cache" 2>/dev/null || stat -f %Lp "$cache")
  [ "$owner" = "$(id -u)" ] && [ "$group" = "$(id -g)" ] && [ "$mode" = 700 ] || \
    die "backup_cache_permissions_invalid" 78
  RESTIC_CACHE_DIR=$cache
  export RESTIC_CACHE_DIR
}

configure_metrics() {
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  case "$metric_dir" in /*) ;; *) die "backup_metrics_path_not_absolute" 78 ;; esac
  [ -d "$metric_dir" ] && [ ! -L "$metric_dir" ] || \
    die "backup_metrics_directory_invalid" 78
  owner=$(stat -c %u "$metric_dir" 2>/dev/null || stat -f %u "$metric_dir")
  group=$(stat -c %g "$metric_dir" 2>/dev/null || stat -f %g "$metric_dir")
  mode=$(stat -c %a "$metric_dir" 2>/dev/null || stat -f %Lp "$metric_dir")
  [ "$owner" = "$(id -u)" ] && [ "$group" = "$(id -g)" ] || \
    die "backup_metrics_permissions_invalid" 78
  case "$mode" in 700|710|711|750|751|755) ;; *) die "backup_metrics_permissions_invalid" 78 ;; esac
  probe="$metric_dir/.saydin_backup_write_probe.$$"
  [ ! -e "$probe" ] || die "backup_metrics_write_probe_exists" 78
  if ! : > "$probe"; then die "backup_metrics_not_writable" 78; fi
  rm -f "$probe" || die "backup_metrics_write_probe_cleanup_failed" 78
}

configure_database() {
  for name in PGHOST PGPORT PGDATABASE PGUSER SAYDIN_DATABASE_ROLE_PREFIX \
    SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256 SAYDIN_BACKUP_DATABASE_PASSWORD_FILE; do require "$name"; done
  case "$SAYDIN_DATABASE_ROLE_PREFIX" in ""|*[!a-z0-9_]*) die "backup_role_prefix_invalid" 78 ;; esac
  case "$PGHOST" in ""|*[!A-Za-z0-9.-]*) die "backup_database_host_invalid" 78 ;; esac
  case "$PGPORT" in ""|*[!0-9]*) die "backup_database_port_invalid" 78 ;; esac
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
  # libpq otherwise permits an unbounded TCP/TLS connection attempt. Individual
  # physical-protocol commands also have explicit wall-clock budgets below.
  PGCONNECT_TIMEOUT=10
  export PGCONNECT_TIMEOUT
}

release_physical_probe_lock() {
  [ -n "$physical_probe_lock_held" ] || return 0
  flock -u 8 >/dev/null 2>&1 || true
  exec 8>&-
  physical_probe_lock_held=
}

try_acquire_physical_probe_lock() {
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  lock="$metric_dir/.saydin_backup_physical_probe.lock"
  if [ -e "$lock" ]; then
    [ -f "$lock" ] && [ ! -L "$lock" ] || die "backup_physical_probe_lock_invalid" 78
    owner=$(stat -c %u "$lock" 2>/dev/null || stat -f %u "$lock")
    group=$(stat -c %g "$lock" 2>/dev/null || stat -f %g "$lock")
    mode=$(stat -c %a "$lock" 2>/dev/null || stat -f %Lp "$lock")
    [ "$owner" = "$(id -u)" ] && [ "$group" = "$(id -g)" ] && [ "$mode" = 600 ] || \
      die "backup_physical_probe_lock_invalid" 78
  else
    : > "$lock"
    chmod 0600 "$lock"
  fi
  exec 8>"$lock"
  if ! flock -n 8; then exec 8>&-; return 1; fi
  physical_probe_lock_held=true
}

acquire_physical_probe_lock() {
  remaining=7200
  until try_acquire_physical_probe_lock; do
    [ "$remaining" -gt 0 ] || die "backup_physical_probe_lock_timeout" 75
    sleep 5 & wait $! || true
    remaining=$((remaining - 5))
  done
}

hex_is_newer() {
  [ "$1" != "$2" ] || return 1
  [ "$(printf '%s\n%s\n' "$1" "$2" | LC_ALL=C sort | tail -n 1)" = "$1" ]
}

ensure_repository() {
  if run_tracked restic snapshots --no-lock >/dev/null 2>&1; then
    return 0
  else
    status=$?
  fi
  case "$status" in
    10) die "backup_repository_not_initialized" 78 ;;
    12) die "backup_repository_authentication_failed" 78 ;;
    *) die "backup_repository_unavailable" 75 ;;
  esac
}

verify_physical_authentication() {
  slot="${SAYDIN_DATABASE_ROLE_PREFIX}_backup_slot_v1"
  slot_length=$(printf %s "$slot" | wc -c | tr -d ' ')
  [ "$slot_length" -le 63 ] || die "backup_replication_slot_invalid" 78
  if run_tracked timeout -s TERM -k 5 30 \
      pg_receivewal --host="$PGHOST" --port="$PGPORT" --username="$PGUSER" \
      --no-password --slot="$slot" --create-slot --if-not-exists \
      >/tmp/backup-auth-physical.log 2>&1; then
    rm -f /tmp/backup-auth-physical.log
    return 0
  fi
  if pg_isready --host="$PGHOST" --port="$PGPORT" --timeout=5 >/dev/null 2>&1; then
    die "backup_physical_authentication_or_configuration_failed" 78
  fi
  die "backup_database_unavailable" 75
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

seconds_until_next_base() {
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  [ -d "$metric_dir" ] && [ ! -L "$metric_dir" ] || \
    die "backup_metrics_directory_invalid" 78
  metric="$metric_dir/saydin_backup_base.prom"
  if [ ! -e "$metric" ]; then
    printf '0\n'
    return 0
  fi
  [ -f "$metric" ] && [ ! -L "$metric" ] || die "backup_base_metric_invalid" 78
  links=$(stat -c %h "$metric" 2>/dev/null || stat -f %l "$metric")
  owner=$(stat -c %u "$metric" 2>/dev/null || stat -f %u "$metric")
  group=$(stat -c %g "$metric" 2>/dev/null || stat -f %g "$metric")
  mode=$(stat -c %a "$metric" 2>/dev/null || stat -f %Lp "$metric")
  [ "$links" = 1 ] && [ "$owner" = "$(id -u)" ] && [ "$group" = "$(id -g)" ] \
    && [ "$mode" = 600 ] || die "backup_base_metric_invalid" 78
  [ "$(wc -l < "$metric" | tr -d ' ')" = 1 ] || die "backup_base_metric_invalid" 78
  IFS= read -r line < "$metric" || die "backup_base_metric_invalid" 78
  prefix='saydin_backup_last_success_timestamp_seconds{kind="base"} '
  case "$line" in "$prefix"*) ;; *) die "backup_base_metric_invalid" 78 ;; esac
  completed_at=${line#"$prefix"}
  case "$completed_at" in ""|*[!0-9]*) die "backup_base_metric_invalid" 78 ;; esac
  [ "${#completed_at}" -le 16 ] && [ "$completed_at" -gt 0 ] || \
    die "backup_base_metric_invalid" 78
  now=$(date +%s)
  [ "$completed_at" -le "$now" ] || die "backup_base_metric_future" 78
  age=$((now - completed_at))
  if [ "$age" -lt 86400 ]; then
    printf '%s\n' $((86400 - age))
  else
    printf '0\n'
  fi
}

write_wal_recovery_metric() {
  recovered_at=$1
  segment_at=$2
  case "$recovered_at:$segment_at" in *[!0-9:]*) die "backup_wal_recovery_timestamp_invalid" 78 ;; esac
  now=$(date +%s)
  [ "$segment_at" -gt 0 ] && [ "$segment_at" -le "$recovered_at" ] \
    && [ "$recovered_at" -le "$now" ] || \
    die "backup_wal_recovery_timestamp_invalid" 78
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  [ -d "$metric_dir" ] && [ ! -L "$metric_dir" ] || \
    die "backup_metrics_directory_missing" 78
  temp="$metric_dir/.saydin_backup_wal.prom.$$"
  if ! {
    printf 'saydin_backup_last_success_timestamp_seconds{kind="wal"} %s\n' "$recovered_at"
    printf 'saydin_backup_wal_last_segment_timestamp_seconds %s\n' "$segment_at"
  } > "$temp"; then
    rm -f "$temp"
    die "backup_wal_recovery_metric_write_failed" 75
  fi
  if ! mv "$temp" "$metric_dir/saydin_backup_wal.prom"; then
    rm -f "$temp"
    die "backup_wal_recovery_metric_publish_failed" 75
  fi
}

write_backup_validity_metric() {
  require SAYDIN_BACKUP_ROLE_VALID_UNTIL_EPOCH_SECONDS
  valid_until_epoch=$SAYDIN_BACKUP_ROLE_VALID_UNTIL_EPOCH_SECONDS
  case "$valid_until_epoch" in ""|*[!0-9]*) die "backup_validity_epoch_invalid" 78 ;; esac
  [ "${#valid_until_epoch}" -le 16 ] && [ "$valid_until_epoch" -gt 0 ] || \
    die "backup_validity_epoch_invalid" 78
  now=$(date +%s)
  [ "$valid_until_epoch" -gt "$now" ] && \
    [ "$valid_until_epoch" -le $((now + 8035200)) ] || die "backup_validity_epoch_unsafe" 78
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  temp="$metric_dir/.saydin_backup_validity.prom.$$"
  if ! printf 'saydin_backup_login_valid_until_timestamp_seconds %s\n' \
      "$valid_until_epoch" > "$temp"; then
    rm -f "$temp"
    die "backup_validity_metric_write_failed" 75
  fi
  if ! mv "$temp" "$metric_dir/saydin_backup_validity.prom"; then
    rm -f "$temp"
    die "backup_validity_metric_publish_failed" 75
  fi
}

cleanup_base_staging() {
  [ -n "$base_target" ] || return 0
  [ -n "$base_staging_root" ] || return 1
  [ "$base_target" = "$base_staging_root/current" ] || return 1
  [ -e "$base_target" ] || [ -L "$base_target" ] || return 0
  [ -d "$base_target" ] && [ ! -L "$base_target" ] || return 1
  owner=$(stat -c %u "$base_target" 2>/dev/null || stat -f %u "$base_target") || return 1
  group=$(stat -c %g "$base_target" 2>/dev/null || stat -f %g "$base_target") || return 1
  mode=$(stat -c %a "$base_target" 2>/dev/null || stat -f %Lp "$base_target") || return 1
  [ "$owner" = "$(id -u)" ] && [ "$group" = "$(id -g)" ] && [ "$mode" = 700 ] || return 1
  rm -rf -- "$base_target"
}

release_base_staging_lock() {
  [ -n "$base_lock_held" ] || return 0
  flock -u 9 >/dev/null 2>&1 || true
  exec 9>&-
  base_lock_held=
}

acquire_base_staging_lock() {
  lock="$base_staging_root/.basebackup.lock"
  if [ -e "$lock" ]; then
    [ -f "$lock" ] && [ ! -L "$lock" ] || die "backup_base_staging_lock_invalid" 78
    owner=$(stat -c %u "$lock" 2>/dev/null || stat -f %u "$lock")
    group=$(stat -c %g "$lock" 2>/dev/null || stat -f %g "$lock")
    mode=$(stat -c %a "$lock" 2>/dev/null || stat -f %Lp "$lock")
    [ "$owner" = 1001 ] && [ "$group" = 1001 ] && [ "$mode" = 600 ] || \
      die "backup_base_staging_lock_invalid" 78
  else
    : > "$lock"
    chmod 0600 "$lock"
  fi
  exec 9>"$lock"
  lock_wait_remaining=7200
  until flock -n 9; do
    [ "$lock_wait_remaining" -gt 0 ] || die "backup_base_staging_lock_timeout" 75
    sleep 5 &
    wait $! || true
    lock_wait_remaining=$((lock_wait_remaining - 5))
  done
  base_lock_held=true
}

configure_base_staging() {
  staging_action=${1:-create}
  case "$staging_action" in create|reconcile) ;; *) die "backup_base_staging_action_invalid" 78 ;; esac
  require SAYDIN_BACKUP_BASE_STAGING_DIR
  require SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES
  [ "$SAYDIN_BACKUP_BASE_STAGING_DIR" = /var/lib/saydin-backup/base-staging ] || \
    die "backup_base_staging_path_invalid" 78
  [ "$(id -u)" = 1001 ] && [ "$(id -g)" = 1001 ] || \
    die "backup_base_staging_process_identity_invalid" 78
  [ -d "$SAYDIN_BACKUP_BASE_STAGING_DIR" ] && [ ! -L "$SAYDIN_BACKUP_BASE_STAGING_DIR" ] || \
    die "backup_base_staging_directory_invalid" 78
  mountpoint -q "$SAYDIN_BACKUP_BASE_STAGING_DIR" || \
    die "backup_base_staging_mount_required" 78
  filesystem=$(awk -v target="$SAYDIN_BACKUP_BASE_STAGING_DIR" \
    '$2 == target { value=$3 } END { print value }' /proc/mounts)
  case "$filesystem" in
    "") die "backup_base_staging_filesystem_unknown" 78 ;;
    tmpfs|ramfs) die "backup_base_staging_disk_required" 78 ;;
  esac
  canonical=$(cd -P -- "$SAYDIN_BACKUP_BASE_STAGING_DIR" && pwd -P) || \
    die "backup_base_staging_canonicalization_failed" 78
  [ "$canonical" = "$SAYDIN_BACKUP_BASE_STAGING_DIR" ] || \
    die "backup_base_staging_canonical_path_required" 78
  owner=$(stat -c %u "$canonical" 2>/dev/null || stat -f %u "$canonical")
  group=$(stat -c %g "$canonical" 2>/dev/null || stat -f %g "$canonical")
  mode=$(stat -c %a "$canonical" 2>/dev/null || stat -f %Lp "$canonical")
  [ "$owner" = 1001 ] && [ "$group" = 1001 ] && [ "$mode" = 700 ] || \
    die "backup_base_staging_permissions_invalid" 78
  case "$SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES" in
    ""|*[!0-9]*) die "backup_base_staging_capacity_invalid" 78 ;;
  esac
  [ "${#SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES}" -le 16 ] || \
    die "backup_base_staging_capacity_invalid" 78
  [ "$SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES" -ge 8589934592 ] || \
    die "backup_base_staging_capacity_invalid" 78

  base_staging_root=$canonical
  acquire_base_staging_lock
  base_target=$base_staging_root/current
  cleanup_base_staging || die "backup_base_staging_cleanup_failed"
  available_kib=$(df -Pk "$base_staging_root" | awk 'NR==2 {print $4}')
  case "$available_kib" in ""|*[!0-9]*) die "backup_base_staging_capacity_unavailable" 78 ;; esac
  available_bytes=$((available_kib * 1024))
  [ "$available_bytes" -ge "$SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES" ] || \
    die "backup_base_staging_capacity_insufficient" 75
  configure_cache "$base_staging_root/restic-cache"
  if [ "$staging_action" = create ]; then
    mkdir -m 0700 "$base_target" || die "backup_base_staging_create_failed" 75
  fi
}

observe_wal_spool_capacity() {
  available_kib=$(df -Pk "$spool" | awk 'NR==2 {print $4}')
  case "$available_kib" in ""|*[!0-9]*) die "backup_wal_spool_capacity_unavailable" 78 ;; esac
  available_bytes=$((available_kib * 1024))
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  temp="$metric_dir/.saydin_backup_wal_spool.prom.$$"
  if ! {
    printf 'saydin_backup_wal_spool_free_bytes %s\n' "$available_bytes"
    printf 'saydin_backup_wal_spool_capacity_floor_bytes %s\n' \
      "$SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES"
  } > "$temp"; then
    rm -f "$temp"
    die "backup_wal_spool_metric_write_failed" 75
  fi
  if ! mv "$temp" "$metric_dir/saydin_backup_wal_spool.prom"; then
    rm -f "$temp"
    die "backup_wal_spool_metric_publish_failed" 75
  fi
  [ "$available_bytes" -ge "$SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES" ] || \
    die "backup_wal_spool_capacity_insufficient" 75
}

configure_wal_spool() {
  require SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES
  spool=/work/wal
  [ "$(id -u)" = 1001 ] && [ "$(id -g)" = 1001 ] || \
    die "backup_wal_spool_process_identity_invalid" 78
  [ -d "$spool" ] && [ ! -L "$spool" ] || die "backup_wal_spool_directory_invalid" 78
  mountpoint -q "$spool" || die "backup_wal_spool_mount_required" 78
  filesystem=$(awk -v target="$spool" \
    '$2 == target { value=$3 } END { print value }' /proc/mounts)
  case "$filesystem" in
    "") die "backup_wal_spool_filesystem_unknown" 78 ;;
    tmpfs|ramfs) die "backup_wal_spool_disk_required" 78 ;;
  esac
  canonical=$(cd -P -- "$spool" && pwd -P) || die "backup_wal_spool_canonicalization_failed" 78
  [ "$canonical" = "$spool" ] || die "backup_wal_spool_canonical_path_required" 78
  owner=$(stat -c %u "$spool" 2>/dev/null || stat -f %u "$spool")
  group=$(stat -c %g "$spool" 2>/dev/null || stat -f %g "$spool")
  mode=$(stat -c %a "$spool" 2>/dev/null || stat -f %Lp "$spool")
  [ "$owner" = 1001 ] && [ "$group" = 1001 ] && [ "$mode" = 700 ] || \
    die "backup_wal_spool_permissions_invalid" 78
  case "$SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES" in
    ""|*[!0-9]*) die "backup_wal_spool_capacity_invalid" 78 ;;
  esac
  [ "${#SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES}" -le 16 ] && \
    [ "$SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES" -ge 103079215104 ] || \
    die "backup_wal_spool_capacity_invalid" 78
  observation_temp="$spool/.saydin-wal-observation.tmp"
  watermark_temp="$spool/.last-offhost-segment.tmp"
  rm -f -- "$observation_temp" "$watermark_temp"
  observe_wal_spool_capacity
}

upload_unverified_wal_snapshot() {
  # A base backup consumes the only spare replication connection. Preserve the
  # completed WAL off-host without publishing a high-water observation or a
  # freshness metric that has not been proven against the server.
  run_tracked restic --retry-lock "$restic_retry_lock" backup "$spool" \
    --exclude='*.partial' --exclude='.restic-cache' --exclude='.last-offhost-segment' \
    --exclude='.last-offhost-segment.tmp' --exclude='.saydin-wal-observation' \
    --exclude='.saydin-wal-observation.tmp' \
    --tag saydin --tag wal-unverified --tag "kms:$SAYDIN_BACKUP_KMS_KEY_ID" \
    --host "$SAYDIN_DEPLOYMENT_ID" --json >/tmp/restic-wal.json || return 75
  run_tracked restic --retry-lock "$restic_retry_lock" forget --tag wal-unverified \
    --keep-within 14d >/dev/null || return 75
  rm -f /tmp/restic-wal.json
}

record_wal_probe_failure() {
  wal_probe_failure_count=$((wal_probe_failure_count + 1))
  if [ "$wal_probe_failure_count" -ge 3 ]; then
    write_failure_metric wal
  fi
}

backup_exit() {
  status=$1
  trap - EXIT HUP INT TERM
  if [ -n "$active_child" ]; then
    kill "$active_child" 2>/dev/null || true
    wait "$active_child" 2>/dev/null || true
    active_child=
  fi
  if [ -n "$receiver" ]; then
    kill "$receiver" 2>/dev/null || true
    wait "$receiver" 2>/dev/null || true
  fi
  [ -z "$pgpass" ] || rm -f "$pgpass"
  rm -f /tmp/backup-auth-physical.log /tmp/backup-auth-sql.log /tmp/pg_basebackup.log \
    /tmp/pg_receivewal.log /tmp/restic-base.json \
    /tmp/restic-wal.json /tmp/restic-base-snapshots.json \
    /tmp/restic-wal-snapshots.json /tmp/restic-wal-selection.json \
    /tmp/restic-wal-observation.json /tmp/backup-wal-highwater.log
  if [ "$spool" = /work/wal ]; then
    rm -f -- "$spool/.saydin-wal-observation.tmp" "$spool/.last-offhost-segment.tmp"
  fi
  if ! cleanup_base_staging; then
    printf '%s\n' 'backup_base_staging_cleanup_failed' >&2
    [ "$status" -ne 0 ] || status=70
  fi
  release_base_staging_lock
  release_physical_probe_lock
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
  configure_metrics
  configure_base_staging create
  ensure_repository
  acquire_physical_probe_lock
  verify_physical_authentication
  export LC_ALL=C
  if run_tracked timeout -s TERM -k 30 7200 \
      pg_basebackup --host="$PGHOST" --port="$PGPORT" --dbname="$PGDATABASE" --username="$PGUSER" \
    --pgdata="$base_target" --format=plain --wal-method=fetch --checkpoint=fast \
      --manifest-checksums=SHA256 --no-password --verbose >/dev/null 2>/tmp/pg_basebackup.log; then
    rm -f /tmp/pg_basebackup.log
  else
    die "backup_base_transfer_failed" 75
  fi
  release_physical_probe_lock
  run_tracked pg_verifybackup "$base_target" >/dev/null || die "backup_manifest_verification_failed"
  system_identifier=$(pg_controldata "$base_target" | sed -n 's/^Database system identifier: *//p')
  case "$system_identifier" in ""|*[!0-9]*) die "backup_system_identifier_missing" ;; esac
  observed_hash=$(printf '%s' "$system_identifier" | sha256sum | cut -d' ' -f1)
  [ "$observed_hash" = "$SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" ] || \
    die "backup_wrong_cluster"
  run_tracked restic --retry-lock "$restic_retry_lock" backup "$base_target" \
    --tag saydin --tag base --tag "kms:$SAYDIN_BACKUP_KMS_KEY_ID" \
    --host "$SAYDIN_DEPLOYMENT_ID" --json >/tmp/restic-base.json || \
    die "backup_base_off_host_write_failed" 75
  run_tracked restic --retry-lock "$restic_retry_lock" forget --tag base \
    --keep-daily 14 --keep-weekly 8 --keep-monthly 12 >/dev/null || \
    die "backup_base_retention_failed" 75
  write_metric base
  cleanup_base_staging || die "backup_base_staging_cleanup_failed"
  base_target=
  release_base_staging_lock
  rm -f /tmp/restic-base.json
}

prune_repository_if_due() {
  metric_dir=${SAYDIN_BACKUP_METRICS_DIR:-/var/lib/node-exporter/textfile}
  [ -d "$metric_dir" ] || die "backup_metrics_directory_missing" 78
  marker="$metric_dir/.saydin_backup_last_prune_epoch"
  now=$(date +%s)
  if [ -e "$marker" ]; then
    [ -f "$marker" ] && [ ! -L "$marker" ] || die "backup_prune_marker_invalid" 78
    owner=$(stat -c %u "$marker" 2>/dev/null || stat -f %u "$marker")
    mode=$(stat -c %a "$marker" 2>/dev/null || stat -f %Lp "$marker")
    [ "$owner" = "$(id -u)" ] && [ "$mode" = 600 ] || die "backup_prune_marker_invalid" 78
    previous=$(cat "$marker")
    case "$previous" in ""|*[!0-9]*) die "backup_prune_marker_invalid" 78 ;; esac
    [ "$now" -ge "$previous" ] || die "backup_prune_clock_regression" 78
    [ $((now - previous)) -ge 604800 ] || return 0
  fi
  configure_repository
  run_tracked restic --no-cache --retry-lock "$restic_retry_lock" prune >/dev/null || return 75
  temp="$metric_dir/.saydin_backup_last_prune_epoch.$$"
  printf '%s' "$now" > "$temp"
  chmod 0600 "$temp"
  mv "$temp" "$marker"
}

base_backup_loop() {
  configure_metrics
  delay=60
  while :; do
    configure_base_staging reconcile
    base_target=
    release_base_staging_lock
    remaining=$(seconds_until_next_base)
    if [ "$remaining" -gt 0 ]; then
      sleep "$remaining" &
      wait $! || true
      continue
    fi
    if "$0" base-backup; then
      delay=60
      if prune_repository_if_due; then
        :
      else
        status=$?
        [ "$status" = 75 ] || exit "$status"
        printf '%s\n' 'backup_repository_prune_deferred' >&2
      fi
    else
      status=$?
      [ "$status" = 75 ] || exit "$status"
      printf 'backup_base_transient_retry_seconds=%s\n' "$delay" >&2
      sleep "$delay" &
      wait $! || true
      if [ "$delay" -lt 900 ]; then
        delay=$((delay * 2))
        [ "$delay" -le 900 ] || delay=900
      fi
    fi
  done
}

wal_stream() {
  configure_repository
  configure_database
  configure_metrics
  require SAYDIN_BACKUP_WAL_UPLOAD_INTERVAL_SECONDS
  [ "$SAYDIN_BACKUP_WAL_UPLOAD_INTERVAL_SECONDS" = 300 ] || \
    die "backup_wal_upload_interval_policy_mismatch" 78
  ensure_repository
  configure_wal_spool
  configure_cache "$spool/.restic-cache"
  acquire_physical_probe_lock
  verify_physical_authentication
  release_physical_probe_lock
  pg_receivewal --directory="$spool" --host="$PGHOST" --port="$PGPORT" --username="$PGUSER" \
    --no-password --synchronous --slot="$slot" \
    >/tmp/pg_receivewal.log 2>&1 &
  receiver=$!
  interval=$SAYDIN_BACKUP_WAL_UPLOAD_INTERVAL_SECONDS
  wal_probe_failure_count=0
  while kill -0 "$receiver" 2>/dev/null; do
    observe_wal_spool_capacity
    sleep "$interval" & wait $! || true
    kill -0 "$receiver" 2>/dev/null || break
    watermark_file="$spool/.last-offhost-segment"
    watermark=
    if [ -e "$watermark_file" ]; then
      [ -f "$watermark_file" ] && [ ! -L "$watermark_file" ] || \
        die "backup_wal_watermark_invalid" 78
      owner=$(stat -c %u "$watermark_file" 2>/dev/null || stat -f %u "$watermark_file")
      mode=$(stat -c %a "$watermark_file" 2>/dev/null || stat -f %Lp "$watermark_file")
      [ "$owner" = "$(id -u)" ] && [ "$mode" = 600 ] || \
        die "backup_wal_watermark_invalid" 78
      watermark=$(cat "$watermark_file")
      case "$watermark" in
        ????????????????????????) case "$watermark" in *[!0-9A-F]*) die "backup_wal_watermark_invalid" 78 ;; esac ;;
        *) die "backup_wal_watermark_invalid" 78 ;;
      esac
    fi
    newest=
    for candidate in "$spool"/*; do
      [ -e "$candidate" ] || continue
      name=${candidate##*/}
      [ -f "$candidate" ] && [ ! -L "$candidate" ] || \
        die "backup_wal_spool_entry_invalid" 78
      case "$name" in
        *.partial|*.history|.saydin-wal-observation) continue ;;
        ????????????????????????)
          case "$name" in *[!0-9A-F]*) die "backup_wal_spool_entry_invalid" 78 ;; esac
          ;;
        *) die "backup_wal_spool_entry_invalid" 78 ;;
      esac
      if [ -z "$newest" ] || hex_is_newer "$name" "$newest"; then newest=$name; fi
    done
    if [ -n "$newest" ]; then
      source_timestamp=$(stat -c %Y "$spool/$newest" 2>/dev/null || \
        stat -f %m "$spool/$newest") || die "backup_wal_recovery_timestamp_unavailable" 78
      observed_timestamp=$(date +%s)
      case "$source_timestamp:$observed_timestamp" in *[!0-9:]*) die "backup_wal_recovery_timestamp_invalid" 78 ;; esac
      [ "$source_timestamp" -gt 0 ] && [ "$source_timestamp" -le "$observed_timestamp" ] || \
        die "backup_wal_recovery_timestamp_invalid" 78
      if ! try_acquire_physical_probe_lock; then
        printf '%s\n' backup_wal_highwater_probe_deferred >&2
        upload_unverified_wal_snapshot || die "backup_wal_unverified_off_host_write_failed" 75
        continue
      fi
      replication_connection="host=$PGHOST port=$PGPORT user=$PGUSER dbname=postgres replication=true"
      if identity=$(timeout -s TERM -k 5 30 \
          psql -X -A -t -F '|' --no-password --dbname="$replication_connection" \
          -c 'IDENTIFY_SYSTEM' 2>/tmp/backup-wal-highwater.log); then :
      else
        release_physical_probe_lock
        printf '%s\n' backup_wal_highwater_probe_unavailable >&2
        record_wal_probe_failure
        upload_unverified_wal_snapshot || die "backup_wal_unverified_off_host_write_failed" 75
        continue
      fi
      if wal_segment_size=$(timeout -s TERM -k 5 30 \
          psql -X -A -t --no-password --dbname="$replication_connection" \
          -c 'SHOW wal_segment_size' 2>>/tmp/backup-wal-highwater.log); then :
      else
        release_physical_probe_lock
        printf '%s\n' backup_wal_highwater_probe_unavailable >&2
        record_wal_probe_failure
        upload_unverified_wal_snapshot || die "backup_wal_unverified_off_host_write_failed" 75
        continue
      fi
      release_physical_probe_lock
      identity_system=${identity%%|*}
      identity_rest=${identity#*|}
      [ "$identity_rest" != "$identity" ] || die "backup_wal_highwater_identity_invalid" 78
      identity_timeline=${identity_rest%%|*}
      identity_rest=${identity_rest#*|}
      identity_lsn=${identity_rest%%|*}
      [ "${identity_rest#*|}" != "$identity_rest" ] || die "backup_wal_highwater_identity_invalid" 78
      case "$identity_system:$identity_timeline:$identity_lsn" in
        *[!0-9A-F/:]*) die "backup_wal_highwater_identity_invalid" 78 ;;
      esac
      identity_hash=$(printf '%s' "$identity_system" | sha256sum | cut -d' ' -f1)
      [ "$identity_hash" = "$SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" ] || \
        die "backup_wal_highwater_wrong_cluster" 78
      highwater=$(saydin-wal-highwater "$identity_timeline" "$identity_lsn" "$wal_segment_size") \
        || die "backup_wal_highwater_invalid" 78
      server_segment=${highwater%%|*}
      server_previous_segment=${highwater#*|}
      if [ "$newest" != "$server_segment" ] && [ "$newest" != "$server_previous_segment" ]; then
        printf '%s\n' backup_wal_receiver_not_caught_up >&2
        record_wal_probe_failure
        upload_unverified_wal_snapshot || die "backup_wal_unverified_off_host_write_failed" 75
        continue
      fi
      wal_probe_failure_count=0
      printf '{"schemaVersion":1,"segment":"%s","segmentSourceTimestamp":%s,"observedTimestamp":%s,"snapshotIncludesSegment":true,"serverTimeline":%s,"serverLsn":"%s","walSegmentSize":"%s","serverWalSegment":"%s","serverPreviousWalSegment":"%s"}\n' \
        "$newest" "$source_timestamp" "$observed_timestamp" "$identity_timeline" "$identity_lsn" \
        "$wal_segment_size" "$server_segment" "$server_previous_segment" > "$observation_temp" \
        || die "backup_wal_observation_write_failed" 75
      chmod 0600 "$observation_temp"
      mv "$observation_temp" "$spool/.saydin-wal-observation" \
        || die "backup_wal_observation_publish_failed" 75
      run_tracked restic --retry-lock "$restic_retry_lock" backup "$spool" \
        --exclude='*.partial' --exclude='.restic-cache' --exclude='.last-offhost-segment' \
        --exclude='.last-offhost-segment.tmp' --exclude='.saydin-wal-observation.tmp' \
        --tag saydin --tag wal --tag wal-observation --tag "kms:$SAYDIN_BACKUP_KMS_KEY_ID" \
        --host "$SAYDIN_DEPLOYMENT_ID" --json >/tmp/restic-wal.json || \
        die "backup_wal_off_host_write_failed" 75
      recovery_timestamp=$source_timestamp
      observation_floor=$((observed_timestamp - 300))
      [ "$observation_floor" -le "$recovery_timestamp" ] || recovery_timestamp=$observation_floor
      write_wal_recovery_metric "$recovery_timestamp" "$source_timestamp"
      if [ -z "$watermark" ] || hex_is_newer "$newest" "$watermark"; then
        printf '%s' "$newest" > "$watermark_temp"
        chmod 0600 "$watermark_temp"
        if ! mv "$watermark_temp" "$watermark_file"; then
          rm -f "$watermark_temp"
          die "backup_wal_watermark_publish_failed" 75
        fi
      fi
      run_tracked restic --retry-lock "$restic_retry_lock" forget --tag wal --keep-within 14d >/dev/null || \
        die "backup_wal_retention_failed" 75
      rm -f /tmp/restic-wal.json
    fi
    find "$spool" -maxdepth 1 -type f \
      \( -name '????????????????????????' -o -name '????????.history' \) \
      ! -name "$newest" \
      -mtime +14 -delete
  done
  wait "$receiver" || die "backup_wal_receiver_failed"
}

verify_auth() {
  configure_database
  configure_metrics
  acquire_physical_probe_lock
  verify_physical_authentication
  replication_connection="host=$PGHOST port=$PGPORT user=$PGUSER dbname=postgres replication=true"
  identity=$(timeout -s TERM -k 5 30 \
    psql -X -A -t -F '|' --no-password --dbname="$replication_connection" \
    -c 'IDENTIFY_SYSTEM') || die "backup_highwater_identity_probe_failed" 78
  wal_segment_size=$(timeout -s TERM -k 5 30 \
    psql -X -A -t --no-password --dbname="$replication_connection" \
    -c 'SHOW wal_segment_size') || die "backup_highwater_segment_size_probe_failed" 78
  identity_system=${identity%%|*}
  identity_rest=${identity#*|}
  [ "$identity_rest" != "$identity" ] || die "backup_highwater_identity_invalid" 78
  identity_timeline=${identity_rest%%|*}
  identity_rest=${identity_rest#*|}
  identity_lsn=${identity_rest%%|*}
  [ "${identity_rest#*|}" != "$identity_rest" ] || die "backup_highwater_identity_invalid" 78
  case "$identity_system:$identity_timeline:$identity_lsn" in
    *[!0-9A-F/:]*) die "backup_highwater_identity_invalid" 78 ;;
  esac
  identity_hash=$(printf '%s' "$identity_system" | sha256sum | cut -d' ' -f1)
  [ "$identity_hash" = "$SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" ] || \
    die "backup_highwater_wrong_cluster" 78
  saydin-wal-highwater "$identity_timeline" "$identity_lsn" "$wal_segment_size" >/dev/null || \
    die "backup_highwater_invalid" 78
  release_physical_probe_lock
  log=/tmp/backup-auth-sql.log
  for database in postgres "$PGDATABASE" template0 template1; do
    if psql -X -A -t --no-password --host="$PGHOST" --port="$PGPORT" \
        --username="$PGUSER" --dbname="$database" -c 'SELECT 1' >"$log" 2>&1; then
      die "backup_sql_access_allowed"
    fi
    pg_isready --host="$PGHOST" --port="$PGPORT" --timeout=5 >/dev/null 2>&1 || \
      die "backup_database_unavailable" 75
  done
  rm -f "$log"
  write_backup_validity_metric
  printf 'backup_auth_physical_highwater_accept_sql_deny_validity_metric_ok\n'
}

restore_snapshot() {
  configure_repository
  configure_cache /tmp/restic-cache
  require SAYDIN_RESTORE_TARGET
  require SAYDIN_RESTORE_CONFIRM
  require SAYDIN_RESTORE_TARGET_TIME
  [ "$SAYDIN_RESTORE_CONFIRM" = "DISPOSABLE_RESTORE_ONLY" ] || die "restore_confirmation_failed" 78
  guarded_target=$(saydin-validate-restore-target "$SAYDIN_RESTORE_TARGET") \
    || die "restore_target_guard_failed" 78
  [ "$guarded_target" = "$SAYDIN_RESTORE_TARGET" ] || die "restore_target_guard_failed" 78
  run_tracked restic --retry-lock "$restic_retry_lock" snapshots --tag base \
    --host "$SAYDIN_DEPLOYMENT_ID" --json > /tmp/restic-base-snapshots.json
  snapshot=$(saydin-select-base-snapshot /tmp/restic-base-snapshots.json "$SAYDIN_RESTORE_TARGET_TIME") \
    || die "restore_base_before_target_missing"
  [ -n "$snapshot" ] || die "restore_base_before_target_missing"
  run_tracked restic --retry-lock "$restic_retry_lock" restore "$snapshot" \
    --target "$SAYDIN_RESTORE_TARGET/base" --verify >/dev/null
  run_tracked restic --retry-lock "$restic_retry_lock" snapshots --tag wal,wal-observation \
    --host "$SAYDIN_DEPLOYMENT_ID" --json > /tmp/restic-wal-snapshots.json
  wal_snapshot=$(saydin-wal-recovery-evidence select /tmp/restic-wal-snapshots.json \
    /tmp/restic-wal-selection.json) \
    || die "restore_wal_observation_missing" 78
  run_tracked restic --retry-lock "$restic_retry_lock" dump "$wal_snapshot" \
    /work/wal/.saydin-wal-observation > /tmp/restic-wal-observation.json \
    || die "restore_wal_observation_dump_failed" 78
  saydin-wal-recovery-evidence preflight /tmp/restic-wal-selection.json \
    /tmp/restic-wal-observation.json || die "restore_wal_recovery_point_stale" 78
  run_tracked restic --retry-lock "$restic_retry_lock" restore "$wal_snapshot" \
    --target "$SAYDIN_RESTORE_TARGET/wal" --verify >/dev/null
  saydin-wal-recovery-evidence evidence /tmp/restic-wal-selection.json "$wal_snapshot" \
    "$SAYDIN_RESTORE_TARGET/wal" "$SAYDIN_RESTORE_TARGET_TIME" \
    /restore-drill/wal-recovery-evidence.json || die "restore_wal_recovery_evidence_invalid" 78
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
