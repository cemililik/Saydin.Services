#!/usr/bin/env python3
from __future__ import annotations

import copy
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
TOOL = ROOT / "infrastructure/release/release_manifest.py"
NAMES = ("api", "backup", "caddy", "calendar", "control", "dqa", "ingestion")


def image(name: str, index: int) -> dict[str, object]:
    hexadecimal = f"{index + 1:064x}"
    return {"name": name, "sourceCommit": "c" * 40,
            "reference": f"ghcr.io/saydin/{name}", "digest": f"sha256:{hexadecimal}",
            "platforms": ["linux/amd64", "linux/arm64"],
            "platformDigests": {"linux/amd64": f"sha256:{index + 101:064x}",
                                "linux/arm64": f"sha256:{index + 201:064x}"},
            "sbom": {"linux/amd64": {"spdxSha256": "a" * 64, "cycloneDxSha256": "b" * 64},
                     "linux/arm64": {"spdxSha256": "c" * 64, "cycloneDxSha256": "d" * 64}}}


def manifest(previous: str | None = None) -> dict[str, object]:
    return {"schemaVersion": 1, "releaseId": "v1.2.3",
            "source": {"repository": "saydin/services", "commitSha": "c" * 40,
                       "workflowRef": "saydin/services/.github/workflows/release-images.yml@refs/heads/main"},
            "database": {"terminalMigration": "022_release_contract", "trustRootSha256": "d" * 64},
            "compatibility": {"minimumMigration": "021_api_trust_expand", "maximumMigration": "022_release_contract",
                              "previousManifestSha256": previous},
            "images": [image(name, index) for index, name in enumerate(NAMES)],
            "runtimeImages": {name: f"registry.invalid/vendor/{name.lower()}@sha256:{index + 301:064x}"
                              for index, name in enumerate(("alertmanager","blackbox","loki","nodeExporter","otel","postgresExporter","prometheus","redis","redisExporter","tempo","timescale"))},
            "backupPolicy": {"rpoMinutes": 15, "rtoMinutes": 120, "walDays": 14,
                             "weeklyWeeks": 8, "monthlyMonths": 12}}


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")


def run(*args: str, expected: int) -> None:
    result = subprocess.run([sys.executable, str(TOOL), *args], capture_output=True, text=True)
    if result.returncode != expected:
        raise AssertionError((args, result.returncode, result.stdout, result.stderr))


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="saydin-release-test-") as raw:
        temp = Path(raw)
        valid = temp / "valid.json"
        write(valid, manifest())
        run("verify", "--manifest", str(valid), expected=0)
        records = temp / "records"; records.mkdir()
        value = manifest()
        for record in value["images"]:  # type: ignore[index]
            write(records / f"{record['name']}.json", record)
        runtime = temp / "runtime.json"; write(runtime, value["runtimeImages"])
        created = temp / "created.json"
        run("create", "--records", str(records), "--runtime-images", str(runtime),
            "--release-id", "v1.2.3", "--repository", "saydin/services", "--commit-sha", "c" * 40,
            "--workflow-ref", "saydin/services/.github/workflows/release-images.yml@refs/heads/main",
            "--terminal-migration", "022_release_contract", "--trust-root-sha256", "d" * 64,
            "--minimum-migration", "021_api_trust_expand", "--maximum-migration", "022_release_contract",
            "--previous-manifest-sha256", "none", "--output", str(created), expected=0)
        run("verify", "--manifest", str(created), expected=0)
        mutations = []
        missing = manifest(); missing["images"] = missing["images"][:-1]  # type: ignore[index]
        mutations.append(missing)
        mutable = manifest(); mutable["images"][0]["digest"] = "latest"  # type: ignore[index]
        mutations.append(mutable)
        placeholder = manifest(); placeholder["releaseId"] = "CHANGE_ME"
        mutations.append(placeholder)
        policy = manifest(); policy["backupPolicy"]["rpoMinutes"] = 60  # type: ignore[index]
        mutations.append(policy)
        range_drift = manifest(); range_drift["compatibility"]["maximumMigration"] = "021_api_trust_expand"  # type: ignore[index]
        mutations.append(range_drift)
        source_drift = manifest(); source_drift["images"][0]["sourceCommit"] = "e" * 40  # type: ignore[index]
        mutations.append(source_drift)
        for index, value in enumerate(mutations):
            path = temp / f"bad-{index}.json"; write(path, value)
            run("verify", "--manifest", str(path), expected=2)
        duplicate = temp / "duplicate.json"
        duplicate.write_text('{"schemaVersion":1,"schemaVersion":1}\n', encoding="utf-8")
        run("verify", "--manifest", str(duplicate), expected=2)

        target = temp / "target.json"; write(target, manifest())
        target_digest = hashlib.sha256(target.read_bytes()).hexdigest()
        current_value = manifest(target_digest); current_value["releaseId"] = "v1.2.4"
        current = temp / "current.json"; write(current, current_value)
        run("verify-rollback", "--current", str(current), "--target", str(target), expected=0)
        wrong = copy.deepcopy(current_value); wrong["compatibility"]["previousManifestSha256"] = "e" * 64  # type: ignore[index]
        wrong_path = temp / "wrong.json"; write(wrong_path, wrong)
        run("verify-rollback", "--current", str(wrong_path), "--target", str(target), expected=2)
    print("release_manifest_self_test_passed:12")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
