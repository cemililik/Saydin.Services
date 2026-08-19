#!/bin/sh
set -eu

fail() {
  printf '%s\n' "calendar_acquisition_rejected:$1" >&2
  exit 64
}

[ "$#" -eq 1 ] || fail "usage"
case "$1" in
  tcmb) plan=${SAYDIN_CALENDAR_TCMB_PLAN:?} ;;
  bist) plan=${SAYDIN_CALENDAR_BIST_PLAN:?} ;;
  *) fail "schedule_unknown" ;;
esac

image=${SAYDIN_CALENDAR_IMAGE:?}
base=${SAYDIN_CALENDAR_BASE_BUNDLE:?}
staging=${SAYDIN_CALENDAR_STAGING_ROOT:?}
lock_file=${SAYDIN_CALENDAR_LOCK_FILE:-/var/lock/saydin-calendar-acquisition.lock}

case "$base:$plan:$staging:$lock_file" in
  /*:/*:/*:/*) ;;
  *) fail "absolute_paths_required" ;;
esac
case "$base$plan$staging$lock_file" in *,*) fail "path_contains_comma" ;; esac
printf '%s\n' "$image" | grep -Eq '^[A-Za-z0-9._/:@-]+@sha256:[0-9a-f]{64}$' \
  || fail "image_digest_required"
[ -d "$base" ] && [ ! -L "$base" ] || fail "base_bundle_unsafe"
[ -f "$plan" ] && [ ! -L "$plan" ] || fail "plan_unsafe"
install -d -m 0700 "$staging"
[ -d "$staging" ] && [ ! -L "$staging" ] || fail "staging_unsafe"
install -d -m 0700 "$(dirname "$lock_file")"

snapshot_set_id=$(jq -er '.snapshotSetId | select(test("^[a-z0-9][a-z0-9._-]{0,79}$"))' "$plan") \
  || fail "snapshot_set_id_invalid"
[ -n "$snapshot_set_id" ] || fail "snapshot_set_id_empty"
output_name="candidate-$snapshot_set_id"

exec 9>"$lock_file"
flock -n 9 || {
  printf '%s\n' "calendar_acquisition_already_running" >&2
  exit 75
}

attempt=1
while [ "$attempt" -le 3 ]; do
  if timeout --signal=TERM --kill-after=30s 15m \
    docker run --rm \
      --read-only \
      --cap-drop ALL \
      --security-opt no-new-privileges \
      --tmpfs /tmp:rw,nosuid,nodev,noexec,size=16m \
      --user "$(id -u):$(id -g)" \
      --mount "type=bind,src=$base,dst=/input/base,readonly" \
      --mount "type=bind,src=$plan,dst=/input/plan.json,readonly" \
      --mount "type=bind,src=$staging,dst=/output" \
      "$image" acquire \
      --base-data-root /input/base \
      --plan /input/plan.json \
      --staging-root /output \
      --output-name "$output_name"
  then
    printf '%s\n' "calendar_acquisition_candidate=$staging/$output_name"
    exit 0
  fi
  [ "$attempt" -lt 3 ] || break
  sleep_seconds=$((attempt * 15))
  sleep "$sleep_seconds"
  attempt=$((attempt + 1))
done

printf '%s\n' "calendar_acquisition_failed_after_3_attempts" >&2
exit 1
