#!/bin/sh
set -eu

fail() {
  printf '%s\n' "calendar_candidate_rejected:$1" >&2
  exit 65
}

[ "$#" -eq 4 ] || fail "usage_candidate_signature_public_key_image"
candidate=$1
signature=$2
public_key=$3
image=$4
reviewer_key_sha256=${SAYDIN_CALENDAR_REVIEWER_PUBLIC_KEY_SHA256:?}
runtime_uid=${SAYDIN_CALENDAR_RUNTIME_UID:-1001}
runtime_gid=${SAYDIN_CALENDAR_RUNTIME_GID:-1001}

case "$candidate:$signature:$public_key" in
  /*:/*:/*) ;;
  *) fail "absolute_paths_required" ;;
esac
case "$candidate$signature$public_key" in *,*) fail "path_contains_comma" ;; esac
printf '%s\n' "$image" | grep -Eq '^[A-Za-z0-9._/:@-]+@sha256:[0-9a-f]{64}$' \
  || fail "image_digest_required"
[ -d "$candidate" ] && [ ! -L "$candidate" ] || fail "candidate_unsafe"
[ -f "$signature" ] && [ ! -L "$signature" ] || fail "signature_unsafe"
[ -f "$public_key" ] && [ ! -L "$public_key" ] || fail "public_key_unsafe"
[ "$(realpath -e "$candidate")" = "$candidate" ] || fail "candidate_not_canonical"
[ "$(realpath -e "$signature")" = "$signature" ] || fail "signature_not_canonical"
[ "$(realpath -e "$public_key")" = "$public_key" ] || fail "public_key_not_canonical"
[ -z "$(find "$candidate" -type l -print -quit)" ] || fail "candidate_contains_symlink"
printf '%s\n' "$reviewer_key_sha256" | grep -Eq '^[0-9a-f]{64}$' \
  || fail "reviewer_key_identity_invalid"
[ "$(sha256sum "$public_key" | awk '{print $1}')" = "$reviewer_key_sha256" ] \
  || fail "reviewer_key_identity_mismatch"

envelope=$candidate/review-envelope.json
manifest=$candidate/source-manifest.json
expected=$candidate/expected-output.json
[ -f "$envelope" ] && [ ! -L "$envelope" ] || fail "envelope_missing"
[ -f "$manifest" ] && [ ! -L "$manifest" ] || fail "manifest_missing"
[ -f "$expected" ] && [ ! -L "$expected" ] || fail "expected_output_missing"

actual_files=$(find "$candidate" -type f -printf '%P\n' | LC_ALL=C sort)
manifest_files=$(jq -er '.sources[].snapshotPath, .calendars[].outputPath' "$manifest") \
  || fail "candidate_file_inventory_invalid"
allowed_files=$(
  {
    printf '%s\n' source-manifest.json expected-output.json review-envelope.json
    printf '%s\n' "$manifest_files"
  } | LC_ALL=C sort -u
) || fail "candidate_file_inventory_invalid"
[ "$actual_files" = "$allowed_files" ] || fail "candidate_contains_untracked_file"

openssl dgst -sha256 -verify "$public_key" -signature "$signature" "$envelope" \
  >/dev/null || fail "signature_invalid"
schema=$(jq -er '.schemaVersion | select(. == 1)' "$envelope") \
  || fail "envelope_schema_invalid"
[ "$schema" = 1 ] || fail "envelope_schema_invalid"
snapshot_set=$(jq -er '.snapshotSetId | select(test("^[a-z0-9][a-z0-9._-]{0,79}$"))' "$envelope") \
  || fail "envelope_snapshot_set_invalid"
manifest_snapshot_set=$(jq -er '.snapshotSetId' "$manifest") || fail "manifest_snapshot_set_invalid"
expected_snapshot_set=$(jq -er '.snapshotSetId' "$expected") || fail "expected_snapshot_set_invalid"
[ "$snapshot_set" = "$manifest_snapshot_set" ] \
  && [ "$snapshot_set" = "$expected_snapshot_set" ] \
  || fail "snapshot_set_mismatch"
manifest_expected=$(jq -er '.sourceManifestSha256 | select(test("^[0-9a-f]{64}$"))' "$envelope") \
  || fail "envelope_manifest_hash_invalid"
output_expected=$(jq -er '.expectedOutputSha256 | select(test("^[0-9a-f]{64}$"))' "$envelope") \
  || fail "envelope_output_hash_invalid"
manifest_actual=$(sha256sum "$manifest" | awk '{print $1}')
output_actual=$(sha256sum "$expected" | awk '{print $1}')
[ "$manifest_actual" = "$manifest_expected" ] || fail "manifest_hash_mismatch"
[ "$output_actual" = "$output_expected" ] || fail "expected_output_hash_mismatch"

candidate_uid=$(stat -c %u "$candidate") || fail "candidate_owner_unreadable"
candidate_gid=$(stat -c %g "$candidate") || fail "candidate_owner_unreadable"
[ "$candidate_uid:$candidate_gid" = "$runtime_uid:$runtime_gid" ] \
  || fail "candidate_owner_identity_mismatch"

docker run --rm --network none \
  --read-only \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  --memory 256m \
  --cpus 1 \
  --pids-limit 128 \
  --user "$candidate_uid:$candidate_gid" \
  --mount "type=bind,src=$candidate,dst=/candidate,readonly" \
  "$image" verify --data-root /candidate

[ "$(sha256sum "$public_key" | awk '{print $1}')" = "$reviewer_key_sha256" ] \
  || fail "reviewer_key_changed"
[ "$(sha256sum "$manifest" | awk '{print $1}')" = "$manifest_expected" ] \
  || fail "manifest_changed"
[ "$(sha256sum "$expected" | awk '{print $1}')" = "$output_expected" ] \
  || fail "expected_output_changed"

printf '%s\n' "calendar_candidate_signature_and_offline_replay_verified"
