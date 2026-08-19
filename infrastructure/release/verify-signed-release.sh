#!/bin/sh
set -eu

if [ "$#" -ne 6 ]; then
  echo "release_verify_usage" >&2
  exit 64
fi

release_dir=$1
certificate_identity_regexp=$2
certificate_oidc_issuer=$3
repository=$4
expected_release=$5
expected_commit=$6

case "$release_dir" in
  /*) ;;
  *) echo "release_dir_absolute_required" >&2; exit 65 ;;
esac
test -d "$release_dir" || { echo "release_dir_missing" >&2; exit 65; }
test -f "$release_dir/release-manifest.json" || { echo "release_manifest_missing" >&2; exit 65; }
test -f "$release_dir/release-manifest.sig" || { echo "release_signature_missing" >&2; exit 65; }
test -f "$release_dir/release-manifest.pem" || { echo "release_certificate_missing" >&2; exit 65; }

python3 infrastructure/release/release_manifest.py verify \
  --manifest "$release_dir/release-manifest.json" >/dev/null

cosign verify-blob \
  --certificate "$release_dir/release-manifest.pem" \
  --signature "$release_dir/release-manifest.sig" \
  --certificate-identity-regexp "$certificate_identity_regexp" \
  --certificate-oidc-issuer "$certificate_oidc_issuer" \
  "$release_dir/release-manifest.json" >/dev/null

python3 - "$release_dir/release-manifest.json" "$repository" "$release_dir" "$expected_release" "$expected_commit" <<'PY'
import hashlib, json, re, subprocess, sys
manifest = json.load(open(sys.argv[1], encoding="utf-8"))
if manifest["source"]["repository"].lower() != sys.argv[2].lower():
    raise SystemExit("release_repository_mismatch")
release_dir = sys.argv[3]
if manifest["releaseId"] != sys.argv[4]: raise SystemExit("release_id_mismatch")
if sys.argv[5] != "any" and manifest["source"]["commitSha"] != sys.argv[5]: raise SystemExit("release_commit_mismatch")
for image in manifest["images"]:
    for platform, short in (("linux/amd64", "amd64"), ("linux/arm64", "arm64")):
        for suffix, field in (("spdx.json", "spdxSha256"), ("cyclonedx.json", "cycloneDxSha256")):
            path = f"{release_dir}/{image['name']}.{short}.{suffix}"
            with open(path, "rb") as stream:
                if hashlib.sha256(stream.read()).hexdigest() != image["sbom"][platform][field]:
                    raise SystemExit("release_sbom_digest_mismatch")
    subprocess.run([
        "cosign", "verify",
        "--certificate-identity-regexp", r"^https://github.com/" + re.escape(sys.argv[2]) + r"/.github/workflows/release-images\.yml@refs/heads/main$",
        "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
        image["reference"] + "@" + image["digest"],
    ], check=True, stdout=subprocess.DEVNULL)
    for digest in image["platformDigests"].values():
        subprocess.run([
            "cosign", "verify",
            "--certificate-identity-regexp", r"^https://github.com/" + re.escape(sys.argv[2]) + r"/.github/workflows/release-images\.yml@refs/heads/main$",
            "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
            image["reference"] + "@" + digest,
        ], check=True, stdout=subprocess.DEVNULL)
        for predicate_type in ("spdxjson", "cyclonedx"):
            subprocess.run([
                "cosign", "verify-attestation", "--type", predicate_type,
                "--certificate-identity-regexp", r"^https://github.com/" + re.escape(sys.argv[2]) + r"/.github/workflows/release-images\.yml@refs/heads/main$",
                "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
                image["reference"] + "@" + digest,
            ], check=True, stdout=subprocess.DEVNULL)
    subprocess.run([
        "gh", "attestation", "verify", "oci://" + image["reference"] + "@" + image["digest"],
        "--repo", sys.argv[2],
    ], check=True, stdout=subprocess.DEVNULL)
PY

echo "signed_release_verified"
