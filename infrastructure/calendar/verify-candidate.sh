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
[ -z "$(find "$candidate" -type l -print -quit)" ] || fail "candidate_contains_symlink"

envelope=$candidate/review-envelope.json
manifest=$candidate/source-manifest.json
expected=$candidate/expected-output.json
[ -f "$envelope" ] && [ ! -L "$envelope" ] || fail "envelope_missing"
[ -f "$manifest" ] && [ ! -L "$manifest" ] || fail "manifest_missing"
[ -f "$expected" ] && [ ! -L "$expected" ] || fail "expected_output_missing"

actual_files=$(find "$candidate" -type f -printf '%P\n' | LC_ALL=C sort)
allowed_files=$(
  {
    printf '%s\n' source-manifest.json expected-output.json review-envelope.json
    jq -r '.sources[].snapshotPath, .calendars[].outputPath' "$manifest"
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

docker run --rm --network none \
  --read-only \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  --mount "type=bind,src=$candidate,dst=/candidate,readonly" \
  "$image" verify --data-root /candidate

printf '%s\n' "calendar_candidate_signature_and_offline_replay_verified"
