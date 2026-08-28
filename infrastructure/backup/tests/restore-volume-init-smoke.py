#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[3]
DOCKERFILE = ROOT / "infrastructure/backup/Dockerfile"
RESOURCE_PREFIX = "saydin-restore-init-smoke-"


def run(*arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["docker", *arguments],
        check=check,
        capture_output=True,
        text=True,
    )


def absent(resource_type: str, name: str) -> bool:
    result = run(resource_type, "inspect", name, check=False)
    if result.returncode == 0:
        return False
    error = result.stderr.lower()
    if "no such" not in error and "not found" not in error:
        raise RuntimeError(f"restore_volume_init_cleanup_inspect_failed:{resource_type}:{name}")
    return True


def runtime_image() -> str:
    matches = re.findall(r"^FROM\s+(\S+)", DOCKERFILE.read_text(encoding="utf-8"), re.MULTILINE)
    if not matches or "@sha256:" not in matches[-1]:
        raise RuntimeError("backup_runtime_image_not_digest_pinned")
    return matches[-1]


def main() -> int:
    if shutil.which("docker") is None or run("info", check=False).returncode != 0:
        print("restore_volume_init_smoke_skipped:docker_unavailable")
        return 77

    suffix = secrets.token_hex(8)
    volume = f"{RESOURCE_PREFIX}{suffix}-data"
    internal_network = f"{RESOURCE_PREFIX}{suffix}-net"
    egress_network = f"{RESOURCE_PREFIX}{suffix}-egress"
    negative_container = f"{RESOURCE_PREFIX}{suffix}-without-chown"
    init_container = f"{RESOURCE_PREFIX}{suffix}-init"
    verify_container = f"{RESOURCE_PREFIX}{suffix}-verify"
    resources = (volume, internal_network, egress_network, negative_container, init_container, verify_container)
    if not all(value.startswith(RESOURCE_PREFIX) for value in resources):
        raise RuntimeError("restore_volume_init_resource_guard_failed")

    image = runtime_image()
    cleanup_errors: list[str] = []
    test_error: BaseException | None = None
    try:
        run("network", "create", "--internal", internal_network)
        run("network", "create", egress_network)
        run("volume", "create", volume)

        without_chown = run(
            "run", "--name", negative_container, "--rm",
            "--user", "0:0", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "-v", f"{volume}:/restore-drill", "--entrypoint", "/bin/sh", image,
            "-c", "chmod 0700 /restore-drill && chown 1001:1001 /restore-drill",
            check=False,
        )
        if without_chown.returncode == 0:
            raise RuntimeError("restore_volume_init_chown_succeeded_without_capability")

        run(
            "run", "--name", init_container, "--rm",
            "--user", "0:0", "--read-only", "--cap-drop", "ALL", "--cap-add", "CHOWN",
            "--security-opt", "no-new-privileges",
            "-v", f"{volume}:/restore-drill", "--entrypoint", "/bin/sh", image,
            "-c", "chmod 0700 /restore-drill && chown 1001:1001 /restore-drill",
        )
        verified = run(
            "run", "--name", verify_container, "--rm",
            "--user", "1001:1001", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "-v", f"{volume}:/restore-drill:ro", "--entrypoint", "/bin/sh", image,
            "-c", "stat -c '%u:%g:%a' /restore-drill",
        ).stdout.strip()
        if verified != "1001:1001:700":
            raise RuntimeError(f"restore_volume_init_metadata_invalid:{verified}")
    except Exception as error:  # cleanup must run before the test is reported failed
        test_error = error
    finally:
        for container in (verify_container, init_container, negative_container):
            result = run("rm", "-f", container, check=False)
            if result.returncode not in (0, 1):
                cleanup_errors.append(f"container:{container}")
        result = run("volume", "rm", volume, check=False)
        if result.returncode != 0:
            cleanup_errors.append(f"volume:{volume}")
        for network in (internal_network, egress_network):
            result = run("network", "rm", network, check=False)
            if result.returncode != 0:
                cleanup_errors.append(f"network:{network}")

    remaining = [
        name for resource_type, name in (
            ("container", negative_container),
            ("container", init_container),
            ("container", verify_container),
            ("volume", volume),
            ("network", internal_network),
            ("network", egress_network),
        )
        if not absent(resource_type, name)
    ]
    if cleanup_errors or remaining:
        print(
            "restore_volume_init_smoke_failed:cleanup:"
            + ",".join(cleanup_errors + remaining),
            file=sys.stderr,
        )
        return 2
    if test_error is not None:
        print(f"restore_volume_init_smoke_failed:{test_error}", file=sys.stderr)
        return 2

    print("restore_volume_init_smoke_passed:owner=1001:1001:mode=700:cleanup=6")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
