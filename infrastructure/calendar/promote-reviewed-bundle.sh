#!/bin/sh
set -eu

fail() {
  printf '%s\n' "calendar_promotion_rejected:$1" >&2
  exit 66
}

[ "$#" -eq 6 ] || fail "usage_candidate_signature_public_key_promotion_root_release_name_image"
candidate=$1
signature=$2
public_key=$3
promotion_root=$4
release_name=$5
image=$6
pending=
verifier=${SAYDIN_CALENDAR_VERIFY_CANDIDATE:?}
staging_root=${SAYDIN_CALENDAR_STAGING_ROOT:?}
runtime_uid=${SAYDIN_CALENDAR_RUNTIME_UID:-1001}
runtime_gid=${SAYDIN_CALENDAR_RUNTIME_GID:-1001}

case "$verifier" in /*) ;; *) fail "verifier_absolute_required" ;; esac
case "$staging_root" in /*) ;; *) fail "staging_root_absolute_required" ;; esac
[ -f "$verifier" ] && [ ! -L "$verifier" ] && [ -x "$verifier" ] \
  || fail "verifier_unsafe"
[ "$(realpath -e "$verifier")" = "$verifier" ] || fail "verifier_not_canonical"
[ "$(id -u):$(id -g)" = "$runtime_uid:$runtime_gid" ] \
  || fail "runtime_identity_mismatch"
[ -d "$staging_root" ] && [ ! -L "$staging_root" ] || fail "staging_root_unsafe"
[ "$(realpath -e "$staging_root")" = "$staging_root" ] || fail "staging_root_not_canonical"
case "$candidate" in
  "$staging_root"/candidate-*) ;;
  *) fail "candidate_not_in_quarantine" ;;
esac
[ -d "$candidate" ] && [ ! -L "$candidate" ] || fail "candidate_unsafe"
[ "$(realpath -e "$candidate")" = "$candidate" ] || fail "candidate_not_canonical"
[ "$(realpath -e "$(dirname "$candidate")")" = "$staging_root" ] \
  || fail "candidate_not_direct_quarantine_child"

cleanup_pending() {
  status=$?
  trap - EXIT HUP INT TERM
  if [ "$status" -ne 0 ] && [ -n "$pending" ]; then
    case "$pending" in
      "$promotion_root"/.pending-"$release_name"-[0-9]*)
        if [ ! -L "$pending" ]; then rm -rf -- "$pending"; fi
        ;;
    esac
  fi
  exit "$status"
}
trap cleanup_pending EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

case "$promotion_root" in /*) ;; *) fail "promotion_root_absolute_required" ;; esac
case "$release_name" in
  *[!a-z0-9._-]*|'') fail "release_name_invalid" ;;
esac
install -d -m 0700 "$promotion_root"
[ -d "$promotion_root" ] && [ ! -L "$promotion_root" ] || fail "promotion_root_unsafe"
[ "$(realpath -e "$promotion_root")" = "$promotion_root" ] || fail "promotion_root_not_canonical"

exec 8>"$promotion_root/.promotion.lock"
flock -n 8 || fail "promotion_already_running"
target=$promotion_root/$release_name
[ ! -e "$target" ] && [ ! -L "$target" ] || fail "release_exists"

"$verifier" "$candidate" "$signature" "$public_key" "$image"

pending=$promotion_root/.pending-$release_name-$$
[ ! -e "$pending" ] || fail "pending_exists"
install -d -m 0700 "$pending"
cp -a "$candidate/." "$pending/"
[ -z "$(find "$pending" -type l -print -quit)" ] || fail "copied_symlink"
find "$pending" -type d -exec chmod 0700 {} +
find "$pending" -type f -exec chmod 0600 {} +
# The source candidate remains mutable quarantine input. Re-run the complete
# signature/envelope/inventory/offline-parser gate against the private copy so
# the directory moved below is the exact byte set that was admitted.
"$verifier" "$pending" "$signature" "$public_key" "$image"
mv -T -n "$pending" "$target"
[ ! -e "$pending" ] && [ -d "$target" ] || fail "atomic_publish_failed"
pending=

# The immutable reviewed copy is now the sole promoted artifact. The explicit,
# fully verified quarantine directory is removed to keep bounded retention.
rm -rf -- "$candidate"
[ ! -e "$candidate" ] || fail "candidate_cleanup_failed"

printf '%s\n' "calendar_reviewed_bundle_promoted=$target"
printf '%s\n' "database_activation_not_performed"
printf '%s\n' "calendar_quarantine_candidate_removed"
