#!/bin/sh
set -eu
umask 077
[ "$#" -eq 2 ] || { echo restore_prepare_usage >&2; exit 64; }
data=$1
target=$2
[ "$data" = /restore-drill/pgdata ] || { echo restore_data_guard_failed >&2; exit 78; }
[ -f "$data/backup_manifest" ] || { echo restore_manifest_missing >&2; exit 78; }
case "$target" in
  ????-??-??T??:??:??Z) ;;
  *) echo restore_target_time_invalid >&2; exit 78 ;;
esac
wal_file=$(find /restore-drill/work/wal -type f ! -name '*.partial' -print -quit)
[ -n "$wal_file" ] || { echo restore_wal_missing >&2; exit 78; }
wal=$(dirname "$wal_file")
printf "restore_command = 'cp %s/%%f %%p'\nrecovery_target_time = '%s'\nrecovery_target_action = 'promote'\n" \
  "$wal" "$target" >> "$data/postgresql.auto.conf"
: > "$data/recovery.signal"
printf '%s\n' restore_recovery_prepared
