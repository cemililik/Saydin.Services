#!/usr/bin/env python3
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

IMAGE = "restic/restic@sha256:39d9072fb5651c80d75c7a811612eb60b4c06b32ffe87c2e9f3c7222e1797e76"


def docker(root: Path, *arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run([
        "docker", "run", "--rm", "--user", "0:0",
        "-e", "RESTIC_REPOSITORY=/fixture/repository",
        "-e", "RESTIC_PASSWORD_FILE=/fixture/password",
        "-v", f"{root}:/fixture", "-v", f"{root / 'wal'}:/work/wal",
        IMAGE, *arguments,
    ], check=check, capture_output=True, text=True)


def cleanup_repository(root: Path) -> None:
    """Remove root-owned restic output before TemporaryDirectory cleans the tree."""
    result = subprocess.run([
        "docker", "run", "--rm", "--user", "0:0",
        "--entrypoint", "/bin/sh", "-v", f"{root}:/fixture", IMAGE,
        "-ec", "rm -rf -- /fixture/repository",
    ], check=False, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError("restic_repository_cleanup_failed")


def main() -> int:
    if shutil.which("docker") is None or subprocess.run(
        ["docker", "info"], capture_output=True, check=False
    ).returncode != 0:
        print("restic_wal_observation_smoke_skipped:docker_unavailable")
        return 77
    with tempfile.TemporaryDirectory(prefix="saydin-restic-wal-") as raw:
        root = Path(raw)
        try:
            (root / "wal").mkdir(mode=0o700)
            (root / "password").write_text("test-repository-password", encoding="utf-8")
            os.chmod(root / "password", 0o400)
            docker(root, "init")
            (root / "wal" / "000000010000000000000001").write_bytes(b"wal")
            docker(root, "backup", "/work/wal", "--tag", "wal", "--host", "test")
            (root / "wal" / ".saydin-wal-observation").write_text("{}\n", encoding="utf-8")
            docker(root, "backup", "/work/wal", "--tag", "wal", "--tag", "wal-observation",
                   "--host", "test")
            selected = json.loads(docker(
                root, "snapshots", "--tag", "wal,wal-observation", "--host", "test", "--json"
            ).stdout)
            if len(selected) != 1:
                raise RuntimeError("restic_tag_intersection_failed")
            identifier = selected[0]["id"]
            dumped = docker(root, "dump", identifier, "/work/wal/.saydin-wal-observation")
            if dumped.stdout != "{}\n":
                raise RuntimeError("restic_observation_dump_failed")
            wrong = docker(
                root, "dump", identifier, "/work/wal-spool/.saydin-wal-observation", check=False
            )
            if wrong.returncode == 0:
                raise RuntimeError("restic_wrong_observation_path_accepted")
        finally:
            cleanup_repository(root)
    print("restic_wal_observation_smoke_passed:tag-intersection,exact-dump-path")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"restic_wal_observation_smoke_failed:{error}")
        raise SystemExit(2)
