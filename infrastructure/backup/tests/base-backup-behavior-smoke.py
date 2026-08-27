#!/usr/bin/env python3
"""Container smoke tests for the base-backup staging and scheduler contracts."""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import uuid


ROOT = Path(__file__).resolve().parents[3]
ENTRYPOINT = ROOT / "infrastructure/backup/backup-entrypoint.sh"
IMAGE = (
    "timescale/timescaledb@"
    "sha256:3adf01543c37b5b88d3c4998338e0f7f21cb3cdd02bbddea08b09bf60e2289b7"
)
PREFIX = "saydin-base-backup-smoke-"


def run(arguments: list[str], *, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(arguments, capture_output=True, text=True, check=False)
    if check and result.returncode != 0:
        print(result.stdout, end="", file=sys.stderr)
        print(result.stderr, end="", file=sys.stderr)
        raise RuntimeError(f"command_failed:{arguments[0]}:{result.returncode}")
    return result


def docker_run_args(volumes: dict[str, str], fake_bin: Path) -> list[str]:
    system_identifier = "123456789"
    environment = {
        "PATH": "/fake-bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        "PGHOST": "database.invalid",
        "PGPORT": "5432",
        "PGDATABASE": "saydin",
        "PGUSER": "test_backup_login_v1",
        "SAYDIN_DATABASE_ROLE_PREFIX": "test",
        "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256": hashlib.sha256(
            system_identifier.encode("ascii")).hexdigest(),
        "SAYDIN_BACKUP_DATABASE_PASSWORD_FILE": "/secrets/database-password",
        "SAYDIN_BACKUP_BUCKET": "test-bucket",
        "SAYDIN_BACKUP_KMS_KEY_ID": "test-kms-key",
        "RESTIC_REPOSITORY": "s3:https://objects.invalid/test-bucket/saydin",
        "RESTIC_PASSWORD_FILE": "/secrets/repository-password",
        "AWS_WEB_IDENTITY_TOKEN_FILE": "/secrets/object-store-token",
        "AWS_ROLE_ARN": "test-role",
        "AWS_REGION": "test-region-1",
        "SAYDIN_BACKUP_RPO_MINUTES": "15",
        "SAYDIN_BACKUP_RTO_MINUTES": "120",
        "SAYDIN_BACKUP_V1_VALID_UNTIL": "2099-01-01T00:00:00Z",
        "SAYDIN_BACKUP_ROLE_VALID_UNTIL_EPOCH_SECONDS": str(int(time.time()) + 60 * 86400),
        "SAYDIN_BACKUP_WAL_UPLOAD_INTERVAL_SECONDS": "300",
        "SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES": str(96 * 1024 * 1024 * 1024),
        "SAYDIN_DEPLOYMENT_ID": "test-deployment",
        "SAYDIN_BACKUP_METRICS_DIR": "/metrics",
        "SAYDIN_BACKUP_BASE_STAGING_DIR": "/var/lib/saydin-backup/base-staging",
        "SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES": str(8 * 1024 * 1024 * 1024),
        "FAKE_WAL_MTIME": "1700000000",
    }
    arguments = [
        "docker", "run", "--rm", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges", "--user", "1001:1001",
        "--tmpfs", "/tmp:uid=1001,gid=1001,mode=0700,size=64m",
        "--mount", f"type=volume,src={volumes['staging']},dst=/var/lib/saydin-backup/base-staging",
        "--mount", f"type=volume,src={volumes['secrets']},dst=/secrets,readonly",
        "--mount", f"type=volume,src={volumes['metrics']},dst=/metrics",
        "--mount", f"type=volume,src={volumes['state']},dst=/state",
        "--mount", f"type=volume,src={volumes['wal']},dst=/work/wal",
        "--mount", f"type=bind,src={ENTRYPOINT},dst=/workspace/backup-entrypoint.sh,readonly",
        "--mount", f"type=bind,src={fake_bin},dst=/fake-bin,readonly",
    ]
    for name, value in environment.items():
        arguments.extend(["--env", f"{name}={value}"])
    return arguments


def inspect(volumes: dict[str, str], command: str) -> subprocess.CompletedProcess[str]:
    return run([
        "docker", "run", "--rm", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges", "--user", "1001:1001",
        "--mount", f"type=volume,src={volumes['staging']},dst=/stage",
        "--mount", f"type=volume,src={volumes['metrics']},dst=/metrics",
        "--mount", f"type=volume,src={volumes['state']},dst=/state",
        "--mount", f"type=volume,src={volumes['wal']},dst=/wal",
        "--entrypoint", "/bin/sh", IMAGE, "-c", command,
    ], check=False)


def cleanup_resources(containers: tuple[str, ...], volumes: dict[str, str]) -> None:
    residual: list[str] = []
    for container in containers:
        run(["docker", "stop", "--time", "1", container], check=False)
        deadline = time.monotonic() + 5
        while run(["docker", "container", "inspect", container], check=False).returncode == 0:
            if time.monotonic() >= deadline:
                residual.append(f"container:{container}")
                break
            time.sleep(0.1)

    for volume in volumes.values():
        deadline = time.monotonic() + 5
        while run(["docker", "volume", "inspect", volume], check=False).returncode == 0:
            removal = run(["docker", "volume", "rm", volume], check=False)
            if removal.returncode == 0:
                continue
            if time.monotonic() >= deadline:
                residual.append(f"volume:{volume}")
                break
            time.sleep(0.1)

    for container in containers:
        if (run(["docker", "container", "inspect", container], check=False).returncode == 0
                and f"container:{container}" not in residual):
            residual.append(f"container:{container}")
    for volume in volumes.values():
        if (run(["docker", "volume", "inspect", volume], check=False).returncode == 0
                and f"volume:{volume}" not in residual):
            residual.append(f"volume:{volume}")
    if residual:
        raise RuntimeError("cleanup_residual:" + ",".join(residual))


def main() -> int:
    if shutil.which("docker") is None or run(["docker", "info"], check=False).returncode != 0:
        print("base_backup_behavior_smoke_skipped:docker_unavailable")
        return 77

    suffix = uuid.uuid4().hex
    volumes = {name: f"{PREFIX}{name}-{suffix}" for name in (
        "staging", "secrets", "metrics", "state", "wal")}
    container_name = f"{PREFIX}scheduler-{suffix}"
    fresh_container_name = f"{PREFIX}fresh-{suffix}"
    signal_container_name = f"{PREFIX}signal-{suffix}"
    wal_container_name = f"{PREFIX}wal-{suffix}"
    for value in (
            *volumes.values(), container_name, fresh_container_name,
            signal_container_name, wal_container_name):
        if not value.startswith(PREFIX) or len(value) > 128:
            raise RuntimeError("unsafe_smoke_resource_name")

    with tempfile.TemporaryDirectory(prefix=PREFIX) as raw:
        fake_bin = Path(raw)
        tools = {
            "restic": """#!/bin/sh
printf 'restic %s\\n' "$*" >>/state/calls
case " ${FAKE_RESTIC_MODE-} $* " in *' fail-backup '*' backup '*) exit 1 ;; esac
case " ${FAKE_RESTIC_MODE-} $* " in *' lock-metrics-on-backup '*' backup '*) chmod 0500 /metrics ;; esac
exit 0
""",
            "pg_basebackup": """#!/bin/sh
target=
for argument in "$@"; do
  case "$argument" in --pgdata=*) target=${argument#--pgdata=} ;; esac
done
[ -n "$target" ] || exit 64
printf 'pg_basebackup %s\\n' "${FAKE_BASE_MODE-success}" >>/state/calls
printf 'partial' >"$target/partial-data"
if [ "${FAKE_BASE_MODE-}" = block ]; then
  : >/state/base-blocking
  exec /bin/sleep 300
fi
if [ "${FAKE_BASE_MODE-}" = auth ]; then
  printf '%s\\n' 'pg_basebackup: error: FATAL: password authentication failed for user "test_backup_login_v1"' >&2
  exit 1
fi
if [ "${FAKE_BASE_MODE-}" = fail-once ] && [ ! -e /state/failed-once ]; then
  : >/state/failed-once
  exit 1
fi
printf 'complete' >"$target/complete-data"
""",
            "pg_verifybackup": """#!/bin/sh
printf 'pg_verifybackup\\n' >>/state/calls
exit 0
""",
            "pg_controldata": """#!/bin/sh
printf 'Database system identifier: 123456789\\n'
""",
            "pg_receivewal": """#!/bin/sh
printf 'pg_receivewal %s\\n' "$*" >>/state/calls
case " $* " in
  *' --create-slot '*)
    if [ "${FAKE_BASE_MODE-}" = auth ]; then
      printf '%s\\n' 'HATA: parola kimlik doğrulaması başarısız oldu' >&2
      exit 1
    fi
    exit 0
    ;;
esac
segment=/work/wal/000000010000000000000001
printf 'completed-wal' >"$segment"
touch -d "@${FAKE_WAL_MTIME}" "$segment"
exec /bin/sleep 300
""",
            "pg_isready": """#!/bin/sh
[ "${FAKE_READY_MODE-}" != unavailable ] || exit 1
exit 0
""",
            "psql": """#!/bin/sh
printf 'psql %s\n' "$*" >>/state/calls
case " $* " in
  *' IDENTIFY_SYSTEM '*) printf '123456789|1|0/01000000|\n'; exit 0 ;;
  *' SHOW wal_segment_size '*) printf '16MB\n'; exit 0 ;;
esac
if [ "${FAKE_SQL_MODE-}" = allow ]; then printf '1\n'; exit 0; fi
printf '%s\n' 'HATA: bu bağlantı türü için pg_hba.conf kaydı yok' >&2
exit 1
""",
            "saydin-wal-highwater": """#!/bin/sh
printf '000000010000000000000001|000000010000000000000000\n'
""",
            "df": """#!/bin/sh
printf 'Filesystem 1024-blocks Used Available Capacity Mounted on\n'
printf 'test 209715200 0 209715200 0%% %s\n' "${2-/work/wal}"
""",
            "sleep": """#!/bin/sh
printf 'sleep %s\\n' "$*" >>/state/calls
if [ "${1-}" = 300 ]; then
  if [ ! -e /state/wal-interval-once ]; then : >/state/wal-interval-once; exit 0; fi
  if [ ! -e /state/wal-interval-twice ]; then : >/state/wal-interval-twice; exit 0; fi
  exec /bin/sleep 300
fi
case "${1-}" in
  ''|*[!0-9]*) ;;
  *) if [ "$1" -ge 3600 ]; then exec /bin/sleep 300; fi ;;
esac
exit 0
""",
        }
        for name, content in tools.items():
            destination = fake_bin / name
            destination.write_text(content, encoding="utf-8")
            os.chmod(destination, 0o755)

        try:
            for volume in volumes.values():
                run(["docker", "volume", "create", volume])
            initialized = run([
                "docker", "run", "--rm", "--user", "0:0",
                "--mount", f"type=volume,src={volumes['staging']},dst=/stage",
                "--mount", f"type=volume,src={volumes['secrets']},dst=/secrets",
                "--mount", f"type=volume,src={volumes['metrics']},dst=/metrics",
                "--mount", f"type=volume,src={volumes['state']},dst=/state",
                "--mount", f"type=volume,src={volumes['wal']},dst=/wal",
                "--entrypoint", "/bin/sh", IMAGE, "-c",
                "chown 1001:1001 /stage /secrets /metrics /state /wal && "
                "chmod 0700 /stage /secrets /metrics /state /wal && "
                "printf database-password-123 >/secrets/database-password && "
                "printf repository-password-123 >/secrets/repository-password && "
                "printf object-store-token-123 >/secrets/object-store-token && "
                "chown 1001:1001 /secrets/* && chmod 0400 /secrets/*",
            ])
            if initialized.returncode != 0:
                raise RuntimeError("volume_initialization_failed")

            common = docker_run_args(volumes, fake_bin)
            success = run(common + [
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "base-backup",
            ], check=False)
            if success.returncode != 0:
                detail = success.stderr.strip().replace("\n", "|")
                raise RuntimeError(f"positive_base_backup_failed:{success.returncode}:{detail}")
            positive = inspect(
                volumes,
                "test ! -e /stage/current && test -d /stage/restic-cache && "
                "test \"$(stat -c %u:%g:%a /stage/restic-cache)\" = 1001:1001:700 && "
                "test -f /metrics/saydin_backup_base.prom && "
                "grep -Fq 'restic --retry-lock 15m backup' /state/calls && "
                "grep -Fq 'restic --retry-lock 15m forget --tag base' /state/calls",
            )
            if positive.returncode != 0:
                evidence = inspect(
                    volumes,
                    "stat -c 'stage=%u:%g:%a' /stage; "
                    "stat -c 'cache=%u:%g:%a' /stage/restic-cache 2>&1 || true; "
                    "find /stage /metrics /state -maxdepth 2 -mindepth 1 -print; "
                    "cat /state/calls 2>&1 || true",
                )
                detail = evidence.stdout.strip().replace("\n", "|")
                raise RuntimeError(f"positive_contract_not_observed:{detail}")

            reset = inspect(
                volumes,
                "rm -f /state/calls /state/failed-once /metrics/saydin_backup_base.prom",
            )
            if reset.returncode != 0:
                raise RuntimeError("state_reset_failed")
            scheduler = common.copy()
            scheduler[2:2] = [
                "--detach", "--name", container_name,
                "--env", "FAKE_BASE_MODE=fail-once",
            ]
            scheduler.extend([
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "base-backup-loop",
            ])
            started = run(scheduler, check=False)
            if started.returncode != 0:
                raise RuntimeError("scheduler_start_failed")
            deadline = time.monotonic() + 15
            observed = False
            while time.monotonic() < deadline:
                probe = inspect(
                    volumes,
                    "test \"$(grep -c '^pg_basebackup ' /state/calls 2>/dev/null || true)\" -ge 2 && "
                    "grep -Fxq 'sleep 60' /state/calls && grep -Eq '^sleep 8[0-9]{4}$' /state/calls",
                )
                if probe.returncode == 0:
                    observed = True
                    break
                time.sleep(0.2)
            if not observed:
                raise RuntimeError("bounded_scheduler_retry_not_observed")
            run(["docker", "stop", "--time", "5", container_name], check=False)
            cleaned = inspect(volumes, "test ! -e /stage/current")
            if cleaned.returncode != 0:
                raise RuntimeError("scheduler_staging_cleanup_failed")

            fresh_reset = inspect(
                volumes,
                "rm -f /state/calls && mkdir -m 0700 /stage/current && "
                "printf crash-residue >/stage/current/partial-data",
            )
            if fresh_reset.returncode != 0:
                raise RuntimeError("fresh_scheduler_state_reset_failed")
            fresh = common.copy()
            fresh[2:2] = ["--detach", "--name", fresh_container_name]
            fresh.extend([
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "base-backup-loop",
            ])
            if run(fresh, check=False).returncode != 0:
                raise RuntimeError("fresh_scheduler_start_failed")
            deadline = time.monotonic() + 15
            fresh_observed = False
            while time.monotonic() < deadline:
                probe = inspect(
                    volumes,
                    "grep -Eq '^sleep 8[0-9]{4}$' /state/calls && "
                    "! grep -q '^pg_basebackup ' /state/calls && test ! -e /stage/current",
                )
                if probe.returncode == 0:
                    fresh_observed = True
                    break
                time.sleep(0.2)
            if not fresh_observed:
                raise RuntimeError("fresh_base_was_not_suppressed")
            run(["docker", "stop", "--time", "5", fresh_container_name], check=False)

            unsafe_setup = inspect(volumes, "ln -s /wal /stage/current && rm -f /state/calls")
            if unsafe_setup.returncode != 0:
                raise RuntimeError("unsafe_residue_setup_failed")
            unsafe = run(common + [
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "base-backup-loop",
            ], check=False)
            if (unsafe.returncode == 0
                    or "backup_base_staging_cleanup_failed" not in unsafe.stderr):
                raise RuntimeError(f"unsafe_residue_not_closed:{unsafe.returncode}")
            unsafe_cleanup = inspect(
                volumes,
                "test -L /stage/current && ! grep -q '^pg_basebackup ' /state/calls && "
                "rm /stage/current",
            )
            if unsafe_cleanup.returncode != 0:
                raise RuntimeError("unsafe_residue_contract_failed")

            signal_reset = inspect(volumes, "rm -f /state/base-blocking")
            if signal_reset.returncode != 0:
                raise RuntimeError("signal_state_reset_failed")
            signaled = common.copy()
            signaled[2:2] = [
                "--detach", "--name", signal_container_name,
                "--env", "FAKE_BASE_MODE=block",
            ]
            signaled.extend([
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "base-backup",
            ])
            if run(signaled, check=False).returncode != 0:
                raise RuntimeError("signal_cleanup_start_failed")
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                signal_ready = inspect(
                    volumes,
                    "test -e /state/base-blocking && test -e /stage/current/partial-data",
                )
                if signal_ready.returncode == 0:
                    break
                time.sleep(0.2)
            else:
                raise RuntimeError("signal_cleanup_target_not_observed")
            if run(["docker", "stop", "--time", "5", signal_container_name],
                   check=False).returncode != 0:
                raise RuntimeError("signal_cleanup_stop_failed")
            signal_cleanup = inspect(volumes, "test ! -e /stage/current")
            if signal_cleanup.returncode != 0:
                raise RuntimeError("signal_staging_cleanup_failed")

            authenticated = run(common[:2] + ["--env", "FAKE_BASE_MODE=auth"] + common[2:] + [
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "base-backup",
            ], check=False)
            if (authenticated.returncode != 78
                    or "backup_physical_authentication_or_configuration_failed" not in authenticated.stderr):
                raise RuntimeError(f"base_authentication_failure_not_closed:{authenticated.returncode}")
            auth_cleanup = inspect(volumes, "test ! -e /stage/current")
            if auth_cleanup.returncode != 0:
                raise RuntimeError("authentication_failure_cleanup_failed")

            verify_auth = run(common + [
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "verify-auth",
            ], check=False)
            if (verify_auth.returncode != 0
                    or "backup_auth_physical_highwater_accept_sql_deny_validity_metric_ok" not in verify_auth.stdout):
                raise RuntimeError(f"localized_sql_deny_not_accepted:{verify_auth.returncode}")
            validity_metric = inspect(
                volumes,
                "test -s /metrics/saydin_backup_validity.prom && "
                "grep -q '^saydin_backup_login_valid_until_timestamp_seconds ' "
                "/metrics/saydin_backup_validity.prom",
            )
            if validity_metric.returncode != 0:
                raise RuntimeError("validity_metric_not_published")
            sql_allowed = run(common[:2] + ["--env", "FAKE_SQL_MODE=allow"] + common[2:] + [
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "verify-auth",
            ], check=False)
            if sql_allowed.returncode == 0 or "backup_sql_access_allowed" not in sql_allowed.stderr:
                raise RuntimeError("sql_allow_not_closed")
            database_unavailable = run(common + [
                "--env", "FAKE_READY_MODE=unavailable",
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "verify-auth",
            ], check=False)
            if (database_unavailable.returncode != 75
                    or "backup_database_unavailable" not in database_unavailable.stderr):
                raise RuntimeError("sql_deny_database_outage_misclassified")
            validity_too_far = run(common + [
                "--env", f"SAYDIN_BACKUP_ROLE_VALID_UNTIL_EPOCH_SECONDS={int(time.time()) + 94 * 86400}",
            ] + [
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "verify-auth",
            ], check=False)
            if (validity_too_far.returncode != 78
                    or "backup_validity_epoch_unsafe" not in validity_too_far.stderr):
                raise RuntimeError("out_of_contract_validity_not_closed")

            missing_metrics = run(common + [
                "--env", "SAYDIN_BACKUP_METRICS_DIR=/missing",
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "base-backup",
            ], check=False)
            if (missing_metrics.returncode != 78
                    or "backup_metrics_directory_invalid" not in missing_metrics.stderr):
                raise RuntimeError(f"missing_metrics_not_closed:{missing_metrics.returncode}")
            missing_metrics_cleanup = inspect(volumes, "test ! -e /stage/current")
            if missing_metrics_cleanup.returncode != 0:
                raise RuntimeError("missing_metrics_staging_cleanup_failed")

            wal_reset = inspect(
                volumes,
                "rm -f /state/calls /state/wal-interval-once /state/wal-interval-twice "
                "/metrics/saydin_backup_wal.prom /wal/.last-offhost-segment "
                "/wal/000000010000000000000001 && "
                "printf stale >/wal/.saydin-wal-observation.tmp && "
                "printf stale >/wal/.last-offhost-segment.tmp",
            )
            if wal_reset.returncode != 0:
                raise RuntimeError("wal_state_reset_failed")
            wal_failure = run(common[:2] + ["--env", "FAKE_RESTIC_MODE=fail-backup"]
                              + common[2:] + [
                                  "--entrypoint", "/workspace/backup-entrypoint.sh",
                                  IMAGE, "wal-stream",
                              ], check=False)
            if wal_failure.returncode != 75:
                raise RuntimeError(f"wal_transient_failure_not_classified:{wal_failure.returncode}")
            wal_failure_state = inspect(
                volumes,
                "test ! -e /metrics/saydin_backup_wal.prom && "
                "test ! -e /wal/.last-offhost-segment",
            )
            if wal_failure_state.returncode != 0:
                raise RuntimeError("failed_wal_upload_advanced_recovery_point")

            metric_failure_setup = inspect(
                volumes,
                "rm -f /state/calls /state/wal-interval-once /state/wal-interval-twice "
                "/metrics/saydin_backup_failure_wal.prom "
                "/wal/000000010000000000000001",
            )
            if metric_failure_setup.returncode != 0:
                raise RuntimeError("metric_failure_setup_failed")
            metric_failure = run(common + [
                "--env", "FAKE_RESTIC_MODE=lock-metrics-on-backup",
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "wal-stream",
            ], check=False)
            if (metric_failure.returncode != 75
                    or "backup_wal_recovery_metric_write_failed" not in metric_failure.stderr):
                raise RuntimeError(f"metric_failure_not_classified:{metric_failure.returncode}")
            metric_failure_state = inspect(
                volumes,
                "test ! -e /metrics/saydin_backup_wal.prom && "
                "test ! -e /wal/.last-offhost-segment && chmod 0700 /metrics",
            )
            if metric_failure_state.returncode != 0:
                raise RuntimeError("failed_metric_publish_advanced_watermark")

            wal_reset = inspect(
                volumes,
                "rm -f /state/calls /state/wal-interval-once /state/wal-interval-twice "
                "/metrics/saydin_backup_failure_wal.prom "
                "/wal/000000010000000000000001",
            )
            if wal_reset.returncode != 0:
                raise RuntimeError("wal_retry_state_reset_failed")
            wal = common.copy()
            wal[2:2] = ["--detach", "--name", wal_container_name]
            wal.extend([
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "wal-stream",
            ])
            if run(wal, check=False).returncode != 0:
                raise RuntimeError("wal_start_failed")
            deadline = time.monotonic() + 15
            wal_observed = False
            while time.monotonic() < deadline:
                probe = inspect(
                    volumes,
                    "test \"$(cat /wal/.last-offhost-segment 2>/dev/null)\" = "
                    "000000010000000000000001 && "
                    "test \"$(grep -c '^saydin_backup_wal_last_segment_timestamp_seconds 1700000000$' "
                    "/metrics/saydin_backup_wal.prom 2>/dev/null || true)\" -eq 1 && "
                    "test \"$(sed -n 's/^saydin_backup_last_success_timestamp_seconds{kind=\"wal\"} //p' "
                    "/metrics/saydin_backup_wal.prom)\" -ge \"$(($(date +%s)-900))\" && "
                    "test \"$(grep -c ' backup /work/wal ' /state/calls "
                    "2>/dev/null || true)\" -eq 2",
                )
                if probe.returncode == 0:
                    wal_observed = True
                    break
                time.sleep(0.2)
            if not wal_observed:
                evidence = inspect(volumes, "cat /state/calls 2>&1; cat /metrics/saydin_backup_wal.prom 2>&1")
                raise RuntimeError("wal_recovery_point_not_observed:" +
                                   evidence.stdout.strip().replace("\n", "|"))
            temp_cleanup = inspect(
                volumes,
                "test ! -e /wal/.saydin-wal-observation.tmp && "
                "test ! -e /wal/.last-offhost-segment.tmp && "
                "test -s /metrics/saydin_backup_wal_spool.prom",
            )
            if temp_cleanup.returncode != 0:
                raise RuntimeError("wal_spool_temp_or_capacity_contract_failed")
            run(["docker", "stop", "--time", "5", wal_container_name], check=False)

            permissions = inspect(volumes, "chmod 0755 /stage")
            if permissions.returncode != 0:
                raise RuntimeError("permission_mutation_failed")
            rejected = run(common + [
                "--entrypoint", "/workspace/backup-entrypoint.sh", IMAGE, "base-backup",
            ], check=False)
            if rejected.returncode != 78 or "backup_base_staging_permissions_invalid" not in rejected.stderr:
                raise RuntimeError(f"unsafe_staging_not_rejected:{rejected.returncode}")
        finally:
            cleanup_resources(
                (container_name, fresh_container_name, signal_container_name, wal_container_name),
                volumes,
            )

    print("base_backup_behavior_smoke_passed")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except RuntimeError as error:
        print(f"base_backup_behavior_smoke_failed:{error}", file=sys.stderr)
        raise SystemExit(2)
