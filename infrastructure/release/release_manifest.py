#!/usr/bin/env python3
"""Create and verify canonical, immutable Saydin release manifests."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path

EXPECTED_IMAGES = ("api", "backup", "caddy", "calendar", "control", "dqa", "ingestion")
EXPECTED_RUNTIME_IMAGES = ("alertmanager", "blackbox", "loki", "nodeExporter", "otel", "postgresExporter", "prometheus", "redis", "redisExporter", "tempo", "timescale")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
MIGRATION = re.compile(r"^(\d{3})_[a-z0-9_]+$")
REF = re.compile(r"^ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+$")
PLACEHOLDER = re.compile(r"(?i)(change[_-]?me|example|placeholder|todo|latest|:<[^>]+>)")


class ManifestError(ValueError):
    pass


def fail(code: str) -> None:
    raise ManifestError(code)


def read_json(path: Path) -> object:
    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_unique_object)
    except (OSError, json.JSONDecodeError, ManifestError) as exc:
        fail(f"invalid_json:{path.name}:{exc}")


def _unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            fail(f"duplicate_key:{key}")
        result[key] = value
    return result


def exact_keys(value: object, expected: set[str], context: str) -> dict[str, object]:
    if not isinstance(value, dict) or set(value) != expected:
        fail(f"invalid_keys:{context}")
    return value


def migration_number(value: object, field: str) -> int:
    if not isinstance(value, str) or not (match := MIGRATION.fullmatch(value)):
        fail(f"invalid_migration:{field}")
    return int(match.group(1))


def verify(manifest: object) -> dict[str, object]:
    root = exact_keys(
        manifest,
        {"schemaVersion", "releaseId", "source", "database", "compatibility", "images", "runtimeImages", "backupPolicy"},
        "manifest",
    )
    if root["schemaVersion"] != 1:
        fail("unsupported_schema")
    serialized = json.dumps(root, sort_keys=True, separators=(",", ":"))
    if PLACEHOLDER.search(serialized):
        fail("placeholder_forbidden")
    if not isinstance(root["releaseId"], str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}", root["releaseId"]):
        fail("invalid_release_id")

    source = exact_keys(root["source"], {"repository", "commitSha", "workflowRef"}, "source")
    if not isinstance(source["repository"], str) or not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", source["repository"]):
        fail("invalid_repository")
    if not isinstance(source["commitSha"], str) or not re.fullmatch(r"[0-9a-f]{40}", source["commitSha"]):
        fail("invalid_commit")
    expected_workflow = f'{source["repository"]}/.github/workflows/release-images.yml@refs/heads/main'
    if source["workflowRef"] != expected_workflow:
        fail("invalid_workflow_ref")

    database = exact_keys(root["database"], {"terminalMigration", "trustRootSha256"}, "database")
    terminal = migration_number(database["terminalMigration"], "terminal")
    if not isinstance(database["trustRootSha256"], str) or not SHA256.fullmatch(database["trustRootSha256"]):
        fail("invalid_trust_root")

    compatibility = exact_keys(
        root["compatibility"],
        {"minimumMigration", "maximumMigration", "previousManifestSha256"},
        "compatibility",
    )
    minimum = migration_number(compatibility["minimumMigration"], "minimum")
    maximum = migration_number(compatibility["maximumMigration"], "maximum")
    if not minimum <= terminal <= maximum:
        fail("incompatible_terminal_range")
    previous = compatibility["previousManifestSha256"]
    if previous is not None and (not isinstance(previous, str) or not SHA256.fullmatch(previous) or previous == "0" * 64):
        fail("invalid_previous_manifest")

    images = root["images"]
    if not isinstance(images, list):
        fail("invalid_images")
    names: list[str] = []
    digests: set[str] = set()
    for index, item in enumerate(images):
        image = exact_keys(item, {"name", "sourceCommit", "reference", "digest", "platforms", "platformDigests", "sbom"}, f"images[{index}]")
        name, reference, digest = image["name"], image["reference"], image["digest"]
        if not isinstance(name, str) or name not in EXPECTED_IMAGES:
            fail("invalid_image_name")
        if not isinstance(reference, str) or not REF.fullmatch(reference):
            fail(f"invalid_image_reference:{name}")
        if not isinstance(digest, str) or not DIGEST.fullmatch(digest):
            fail(f"invalid_image_digest:{name}")
        if image["sourceCommit"] != source["commitSha"]:
            fail(f"image_source_commit_mismatch:{name}")
        if image["platforms"] != ["linux/amd64", "linux/arm64"]:
            fail(f"invalid_platforms:{name}")
        platform_digests = exact_keys(image["platformDigests"], {"linux/amd64", "linux/arm64"}, f"platformDigests:{name}")
        if any(not isinstance(value, str) or not DIGEST.fullmatch(value) for value in platform_digests.values()):
            fail(f"invalid_platform_digest:{name}")
        if len(set(platform_digests.values())) != 2 or digest in platform_digests.values():
            fail(f"invalid_platform_digest_set:{name}")
        sbom = exact_keys(image["sbom"], {"linux/amd64", "linux/arm64"}, f"sbom:{name}")
        for platform, value in sbom.items():
            pair = exact_keys(value, {"spdxSha256", "cycloneDxSha256"}, f"sbom:{name}:{platform}")
            if any(not isinstance(item, str) or not SHA256.fullmatch(item) for item in pair.values()):
                fail(f"invalid_sbom:{name}:{platform}")
        names.append(name)
        if digest in digests:
            fail("duplicate_image_digest")
        digests.add(digest)
    if tuple(sorted(names)) != EXPECTED_IMAGES:
        fail("image_set_mismatch")

    runtime_images = exact_keys(root["runtimeImages"], set(EXPECTED_RUNTIME_IMAGES), "runtimeImages")
    runtime_pattern = re.compile(r"^[a-z0-9.-]+(?:/[a-z0-9_.-]+)+@sha256:[0-9a-f]{64}$")
    if any(not isinstance(value, str) or not runtime_pattern.fullmatch(value) for value in runtime_images.values()):
        fail("runtime_image_not_digest_pinned")
    if len(set(runtime_images.values())) != len(EXPECTED_RUNTIME_IMAGES):
        fail("runtime_image_digest_reused")

    policy = exact_keys(root["backupPolicy"], {"rpoMinutes", "rtoMinutes", "walDays", "weeklyWeeks", "monthlyMonths"}, "backupPolicy")
    if policy != {"rpoMinutes": 15, "rtoMinutes": 120, "walDays": 14, "weeklyWeeks": 8, "monthlyMonths": 12}:
        fail("backup_policy_mismatch")
    return root


def canonical_bytes(manifest: object) -> bytes:
    verified = verify(manifest)
    return (json.dumps(verified, sort_keys=True, separators=(",", ":"), ensure_ascii=False) + "\n").encode()


def create(args: argparse.Namespace) -> None:
    records = []
    record_dir = Path(args.records)
    for name in EXPECTED_IMAGES:
        record_path = record_dir / f"{name}.json"
        record = read_json(record_path)
        if not isinstance(record, dict):
            fail(f"invalid_image_record:{record_path.name}")
        records.append(record)
    previous = None if args.previous_manifest_sha256 == "none" else args.previous_manifest_sha256
    manifest = {
        "schemaVersion": 1,
        "releaseId": args.release_id,
        "source": {"repository": args.repository, "commitSha": args.commit_sha, "workflowRef": args.workflow_ref},
        "database": {"terminalMigration": args.terminal_migration, "trustRootSha256": args.trust_root_sha256},
        "compatibility": {
            "minimumMigration": args.minimum_migration,
            "maximumMigration": args.maximum_migration,
            "previousManifestSha256": previous,
        },
        "images": records,
        "runtimeImages": read_json(Path(args.runtime_images)),
        "backupPolicy": {"rpoMinutes": 15, "rtoMinutes": 120, "walDays": 14, "weeklyWeeks": 8, "monthlyMonths": 12},
    }
    output = Path(args.output)
    output.write_bytes(canonical_bytes(manifest))
    print(hashlib.sha256(output.read_bytes()).hexdigest())


def verify_file(args: argparse.Namespace) -> None:
    path = Path(args.manifest)
    actual = path.read_bytes()
    canonical = canonical_bytes(read_json(path))
    if actual != canonical:
        fail("manifest_not_canonical")
    digest = hashlib.sha256(actual).hexdigest()
    if args.expected_sha256 and digest != args.expected_sha256:
        fail("manifest_digest_mismatch")
    print(digest)


def verify_rollback(args: argparse.Namespace) -> None:
    current_path, target_path = Path(args.current), Path(args.target)
    current = verify(read_json(current_path))
    target = verify(read_json(target_path))
    target_digest = hashlib.sha256(canonical_bytes(target)).hexdigest()
    previous = current["compatibility"]["previousManifestSha256"]  # type: ignore[index]
    if previous != target_digest:
        fail("target_is_not_signed_previous_manifest")
    current_terminal = migration_number(current["database"]["terminalMigration"], "current")  # type: ignore[index]
    target_minimum = migration_number(target["compatibility"]["minimumMigration"], "target_minimum")  # type: ignore[index]
    target_maximum = migration_number(target["compatibility"]["maximumMigration"], "target_maximum")  # type: ignore[index]
    if not target_minimum <= current_terminal <= target_maximum:
        fail("rollback_schema_incompatible")
    print(target_digest)


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    make = sub.add_parser("create")
    make.add_argument("--records", required=True)
    make.add_argument("--runtime-images", required=True)
    make.add_argument("--release-id", required=True)
    make.add_argument("--repository", required=True)
    make.add_argument("--commit-sha", required=True)
    make.add_argument("--workflow-ref", required=True)
    make.add_argument("--terminal-migration", required=True)
    make.add_argument("--trust-root-sha256", required=True)
    make.add_argument("--minimum-migration", required=True)
    make.add_argument("--maximum-migration", required=True)
    make.add_argument("--previous-manifest-sha256", required=True)
    make.add_argument("--output", required=True)
    make.set_defaults(func=create)
    check = sub.add_parser("verify")
    check.add_argument("--manifest", required=True)
    check.add_argument("--expected-sha256")
    check.set_defaults(func=verify_file)
    rollback = sub.add_parser("verify-rollback")
    rollback.add_argument("--current", required=True)
    rollback.add_argument("--target", required=True)
    rollback.set_defaults(func=verify_rollback)
    args = parser.parse_args()
    try:
        args.func(args)
        return 0
    except (ManifestError, OSError) as exc:
        print(f"release_manifest_invalid:{exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
