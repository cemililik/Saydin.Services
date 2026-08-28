#!/usr/bin/env python3
"""Prove archive_timeout closes a real pg_receivewal partial segment."""

from __future__ import annotations

import re
import secrets
import shutil
import subprocess
import sys
import time

IMAGE = (
    "timescale/timescaledb@"
    "sha256:3adf01543c37b5b88d3c4998338e0f7f21cb3cdd02bbddea08b09bf60e2289b7"
)
PREFIX = "saydin-archive-timeout-smoke-"
WAL = re.compile(r"^[0-9A-F]{24}$")


def docker(*arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["docker", *arguments], check=check, capture_output=True, text=True
    )


def inspect(kind: str, name: str) -> int:
    result = docker(kind, "inspect", name, check=False)
    if result.returncode == 0:
        return 0
    if docker("info", check=False).returncode != 0:
        return 2
    return 1


def completed(receiver: str) -> set[str]:
    listing = docker("exec", receiver, "sh", "-c", "ls -1 /wal 2>/dev/null || true").stdout
    return {line for line in listing.splitlines() if WAL.fullmatch(line)}


def partials(receiver: str) -> set[str]:
    listing = docker("exec", receiver, "sh", "-c", "ls -1 /wal 2>/dev/null || true").stdout
    return {
        line.removesuffix(".partial") for line in listing.splitlines()
        if line.endswith(".partial") and WAL.fullmatch(line.removesuffix(".partial"))
    }


def wait_until(predicate, seconds: int, failure: str) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.5)
    raise RuntimeError(failure)


def main() -> int:
    if shutil.which("docker") is None or docker("info", check=False).returncode != 0:
        print("archive_timeout_receiver_smoke_skipped:docker_unavailable")
        return 77

    suffix = secrets.token_hex(8)
    database = f"{PREFIX}{suffix}-db"
    receiver = f"{PREFIX}{suffix}-receiver"
    network = f"{PREFIX}{suffix}-net"
    data_volume = f"{PREFIX}{suffix}-data"
    wal_volume = f"{PREFIX}{suffix}-wal"
    resources = (database, receiver, network, data_volume, wal_volume)
    if not all(value.startswith(PREFIX) and len(value) <= 128 for value in resources):
        raise RuntimeError("archive_timeout_resource_guard_failed")
    for kind, name in (
        ("container", database), ("container", receiver), ("network", network),
        ("volume", data_volume), ("volume", wal_volume),
    ):
        state = inspect(kind, name)
        if state != 1:
            raise RuntimeError(f"archive_timeout_resource_not_absent:{kind}:{state}")

    test_error: Exception | None = None
    cleanup_errors: list[str] = []
    observed: tuple[str, str, int, int] | None = None
    try:
        docker("network", "create", "--internal", network)
        docker("volume", "create", data_volume)
        docker("volume", "create", wal_volume)
        docker(
            "run", "-d", "--name", database, "--network", network,
            "--network-alias", "postgres", "-e", "POSTGRES_HOST_AUTH_METHOD=trust",
            "-e", "POSTGRES_USER=postgres", "-e", "POSTGRES_DB=probe",
            "-v", f"{data_volume}:/var/lib/postgresql/data", IMAGE,
            "postgres", "-c", "archive_timeout=30s", "-c", "checkpoint_timeout=30s",
            "-c", "wal_level=replica", "-c", "log_min_messages=debug1",
        )

        postmaster_started = ""
        def final_postmaster_ready() -> bool:
            nonlocal postmaster_started
            logs = docker("logs", database, check=False).stdout + docker(
                "logs", database, check=False
            ).stderr
            if "PostgreSQL init process complete; ready for start up." not in logs:
                return False
            query = docker(
                "exec", database, "psql", "-X", "-A", "-t", "-U", "postgres",
                "-d", "probe", "-c", "SELECT pg_postmaster_start_time();", check=False,
            )
            if query.returncode != 0 or not query.stdout.strip():
                return False
            process = docker(
                "exec", database, "sh", "-c", "cat /proc/1/comm", check=False
            )
            if process.returncode != 0 or process.stdout.strip() != "postgres":
                return False
            postmaster_started = query.stdout.strip()
            return True

        wait_until(final_postmaster_ready, 60, "archive_timeout_final_postmaster_not_ready")
        settings = docker(
            "exec", database, "psql", "-X", "-A", "-t", "-F", "|",
            "-U", "postgres", "-d", "probe", "-c",
            "SELECT current_setting('archive_timeout'),current_setting('checkpoint_timeout'),"
            "current_setting('archive_mode'),current_setting('wal_segment_size');",
        ).stdout.strip()
        if settings != "30s|30s|off|16MB":
            raise RuntimeError(f"archive_timeout_settings_invalid:{settings}")

        hba = docker(
            "exec", database, "psql", "-X", "-A", "-t", "-U", "postgres",
            "-d", "probe", "-c", "SHOW hba_file",
        ).stdout.strip()
        if not hba.startswith("/") or not hba.endswith("/pg_hba.conf"):
            raise RuntimeError("archive_timeout_hba_path_invalid")
        docker(
            "exec", database, "sh", "-eu", "-c",
            'printf "%s\\n" "host replication all 0.0.0.0/0 trust" >> "$1"',
            "sh", hba,
        )
        if docker(
            "exec", database, "psql", "-X", "-A", "-t", "-U", "postgres",
            "-d", "probe", "-c", "SELECT pg_reload_conf();",
        ).stdout.strip() != "t":
            raise RuntimeError("archive_timeout_hba_reload_failed")

        docker(
            "run", "-d", "--name", receiver, "--network", network,
            "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "-v", f"{wal_volume}:/wal", "--entrypoint", "pg_receivewal", IMAGE,
            "--directory=/wal", "--host=postgres", "--port=5432", "--username=postgres",
            "--no-password", "--synchronous",
        )
        wait_until(
            lambda: docker(
                "exec", database, "psql", "-X", "-A", "-t", "-U", "postgres",
                "-d", "probe", "-c", "SELECT count(*) FROM pg_stat_replication;",
                check=False,
            ).stdout.strip() == "1",
            20, "archive_timeout_receiver_not_ready",
        )

        before_switch = completed(receiver)
        docker(
            "exec", database, "psql", "-X", "-A", "-t", "-U", "postgres",
            "-d", "probe", "-c", "SELECT pg_switch_wal();",
        )
        wait_until(
            lambda: bool(completed(receiver) - before_switch),
            20, "archive_timeout_baseline_switch_not_received",
        )
        baseline = completed(receiver)
        forced_before = (docker("logs", database).stdout + docker("logs", database).stderr).count(
            "write-ahead log switch forced (archive_timeout=30)"
        )

        started = time.monotonic()
        docker(
            "exec", database, "psql", "-X", "-v", "ON_ERROR_STOP=1",
            "-U", "postgres", "-d", "probe", "-c",
            "CREATE TABLE archive_timeout_probe(id bigint PRIMARY KEY,payload text NOT NULL);"
            "INSERT INTO archive_timeout_probe VALUES (1,repeat('x',8192));",
        )
        target_partial: set[str] = set()
        wait_until(
            lambda: bool((target_partial.update(partials(receiver) - baseline) or target_partial)),
            10, "archive_timeout_partial_not_observed",
        )
        if len(target_partial) != 1:
            raise RuntimeError("archive_timeout_partial_ambiguous")
        target = next(iter(target_partial))

        wait_until(
            lambda: target in completed(receiver) and target not in partials(receiver),
            50, "archive_timeout_completed_rename_missing",
        )
        elapsed = int(time.monotonic() - started)
        size = int(docker("exec", receiver, "stat", "-c", "%s", f"/wal/{target}").stdout)
        if size != 16 * 1024 * 1024:
            raise RuntimeError(f"archive_timeout_completed_size_invalid:{size}")
        forced_after = (docker("logs", database).stdout + docker("logs", database).stderr).count(
            "write-ahead log switch forced (archive_timeout=30)"
        )
        if forced_after <= forced_before:
            raise RuntimeError("archive_timeout_forced_switch_log_missing")
        if inspect("container", receiver) != 0:
            raise RuntimeError("archive_timeout_receiver_died")
        observed = (postmaster_started, target, size, elapsed)
    except Exception as error:
        test_error = error
    finally:
        for name in (receiver, database):
            deadline = time.monotonic() + 10
            while inspect("container", name) == 0 and time.monotonic() < deadline:
                docker("rm", "-f", name, check=False)
                time.sleep(0.1)
            if inspect("container", name) != 1:
                cleanup_errors.append(f"container:{name}")
        for name in (wal_volume, data_volume):
            deadline = time.monotonic() + 10
            while inspect("volume", name) == 0 and time.monotonic() < deadline:
                docker("volume", "rm", name, check=False)
                time.sleep(0.1)
            if inspect("volume", name) != 1:
                cleanup_errors.append(f"volume:{name}")
        deadline = time.monotonic() + 10
        while inspect("network", network) == 0 and time.monotonic() < deadline:
            docker("network", "rm", network, check=False)
            time.sleep(0.1)
        if inspect("network", network) != 1:
            cleanup_errors.append(f"network:{network}")

    if cleanup_errors:
        raise RuntimeError("archive_timeout_cleanup_residual:" + ",".join(cleanup_errors))
    if test_error is not None:
        raise test_error
    if observed is None:
        raise RuntimeError("archive_timeout_observation_missing")
    postmaster, segment, size, elapsed = observed
    print(
        "archive_timeout_receiver_smoke_passed:"
        f"settings=archive_timeout_30s,checkpoint_timeout_30s,archive_mode_off:"
        f"postmaster={postmaster}:segment={segment}:size={size}:elapsed={elapsed}s:residual=0"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"archive_timeout_receiver_smoke_failed:{error}", file=sys.stderr)
        raise SystemExit(2)
