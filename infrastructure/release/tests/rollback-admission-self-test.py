#!/usr/bin/env python3
"""Prove the rollback mutation primitive cannot bypass release signature admission."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import subprocess
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
FIXTURE_PATH = ROOT / "infrastructure/release/tests/release-manifest-self-test.py"
ROLLBACK = ROOT / "infrastructure/release/rollback-release.sh"


def load_fixture():
    spec = importlib.util.spec_from_file_location("release_manifest_fixture", FIXTURE_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError("rollback_fixture_load_failed")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def write_executable(path: Path, content: str) -> None:
    path.write_text(content, encoding="utf-8")
    path.chmod(0o700)


def materialize_release(directory: Path, value: dict[str, object], signature: str) -> None:
    directory.mkdir(mode=0o700)
    sbom_content = b"{}\n"
    sbom_digest = hashlib.sha256(sbom_content).hexdigest()
    for image in value["images"]:  # type: ignore[index]
        for platform, short in (("linux/amd64", "amd64"), ("linux/arm64", "arm64")):
            image["sbom"][platform]["spdxSha256"] = sbom_digest  # type: ignore[index]
            image["sbom"][platform]["cycloneDxSha256"] = sbom_digest  # type: ignore[index]
            for suffix in ("spdx.json", "cyclonedx.json"):
                (directory / f"{image['name']}.{short}.{suffix}").write_bytes(sbom_content)
    (directory / "release-manifest.json").write_text(
        json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
    (directory / "release-manifest.sig").write_text(signature, encoding="utf-8")
    (directory / "release-manifest.pem").write_text("fixture-certificate", encoding="utf-8")


def main() -> int:
    fixture = load_fixture()
    with tempfile.TemporaryDirectory(prefix="saydin-rollback-admission-") as raw:
        temp = Path(raw)
        target_dir = temp / "target"
        target = fixture.manifest()
        materialize_release(target_dir, target, "tampered")
        target_digest = hashlib.sha256((target_dir / "release-manifest.json").read_bytes()).hexdigest()

        current_dir = temp / "current"
        current = fixture.manifest(target_digest)
        current["releaseId"] = "v1.2.4"
        current["source"]["commitSha"] = "e" * 40  # type: ignore[index]
        for image in current["images"]:  # type: ignore[index]
            image["sourceCommit"] = "e" * 40
        materialize_release(current_dir, current, "valid")

        fake_bin = temp / "bin"; fake_bin.mkdir(mode=0o700)
        cosign_log = temp / "cosign.log"
        docker_marker = temp / "docker-called"
        write_executable(fake_bin / "cosign", """#!/bin/sh
printf '%s\n' "$*" >> "$SAYDIN_TEST_COSIGN_LOG"
case "$*" in *target/release-manifest.sig*) exit 1 ;; *) exit 0 ;; esac
""")
        write_executable(fake_bin / "gh", "#!/bin/sh\nexit 0\n")
        write_executable(fake_bin / "docker", """#!/bin/sh
: > "$SAYDIN_TEST_DOCKER_MARKER"
exit 99
""")

        compose = temp / "compose.yml"; compose.write_text("services: {}\n", encoding="utf-8")
        current_env = temp / "current.env"; current_env.write_text("X=1\n", encoding="utf-8")
        target_env = temp / "target.env"; target_env.write_text("X=1\n", encoding="utf-8")
        receipt = temp / "receipt"
        environment = dict(os.environ)
        environment.update({
            "PATH": f"{fake_bin}:{environment['PATH']}",
            "SAYDIN_TEST_COSIGN_LOG": str(cosign_log),
            "SAYDIN_TEST_DOCKER_MARKER": str(docker_marker),
        })
        command = [
            str(ROLLBACK), "saydin-production", str(compose), str(current_env), str(target_env),
            str(current_dir), str(target_dir), str(receipt), "12345", "INC-123",
            "v1.2.4", "v1.2.3", "saydin/services", "e" * 40, "c" * 40,
        ]
        result = subprocess.run(
            command, cwd=ROOT, env=environment, capture_output=True, text=True, check=False)
        log = cosign_log.read_text(encoding="utf-8") if cosign_log.exists() else ""
        passed = (
            result.returncode == 78
            and "rollback_target_signature_invalid" in result.stderr
            and "current/release-manifest.sig" in log
            and "target/release-manifest.sig" in log
            and not docker_marker.exists()
            and not receipt.exists()
        )
        if not passed:
            print("rollback_admission_self_test_failed", file=sys.stderr)
            print(result.stdout, result.stderr, log, file=sys.stderr)
            return 2
    print("rollback_admission_self_test_passed:target_signature_tamper_pre_mutation")
    return 0


if __name__ == "__main__":
    import sys
    raise SystemExit(main())
