#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[3]
entry = (ROOT / "infrastructure/backup/backup-entrypoint.sh").read_text(encoding="utf-8")
restore = (ROOT / "infrastructure/backup/restore-drill.sh").read_text(encoding="utf-8")
dockerfile = (ROOT / "infrastructure/backup/Dockerfile").read_text(encoding="utf-8")
production_compose = (ROOT / "infrastructure/deployment/compose.production.yml").read_text(
    encoding="utf-8")
selector = ROOT / "infrastructure/backup/select-base-snapshot.py"
target_guard_path = ROOT / "infrastructure/backup/restore_target_guard.py"
wal_highwater_path = ROOT / "infrastructure/backup/wal-highwater.py"
volume_init_smoke = ROOT / "infrastructure/backup/tests/restore-volume-init-smoke.py"
base_backup_smoke = ROOT / "infrastructure/backup/tests/base-backup-behavior-smoke.py"
wal_evidence_smoke = ROOT / "infrastructure/backup/tests/wal-recovery-evidence-self-test.py"
restic_wal_smoke = ROOT / "infrastructure/backup/tests/restic-wal-observation-smoke.py"
restore_cleanup_smoke = ROOT / "infrastructure/backup/tests/restore-cleanup-behavior-self-test.py"
archive_timeout_smoke = ROOT / "infrastructure/backup/tests/archive-timeout-receiver-smoke.py"
backup_auth_acceptance = (ROOT / ".github/scripts/run-backup-auth-tests.sh").read_text(
    encoding="utf-8")
EXPECTED_CHECK_COUNT = 64
require_docker_smokes_value = os.environ.get("SAYDIN_REQUIRE_DOCKER_SMOKES", "0")
if require_docker_smokes_value not in {"0", "1"}:
    raise SystemExit("backup_static_failed:invalid_require_docker_smokes")
require_docker_smokes = require_docker_smokes_value == "1"
skipped: list[str] = []


def record_smoke(
    checks: dict[str, bool],
    name: str,
    result: subprocess.CompletedProcess[str],
    *,
    docker_bound: bool,
) -> None:
    if result.returncode == 77 and docker_bound:
        skipped.append(name)
        checks[name] = not require_docker_smokes
        return
    checks[name] = result.returncode == 0
    if result.returncode != 0:
        print(result.stdout, end="", file=sys.stderr)
        print(result.stderr, end="", file=sys.stderr)


guard_spec = importlib.util.spec_from_file_location("restore_target_guard", target_guard_path)
if guard_spec is None or guard_spec.loader is None:
    raise SystemExit("backup_static_failed:restore_target_guard_load")
target_guard = importlib.util.module_from_spec(guard_spec)
guard_spec.loader.exec_module(target_guard)


def guard_rejects(root: Path, target: str, *, expected_uid: int | None = None) -> bool:
    try:
        target_guard.prepare_restore_target(root, target, expected_uid=expected_uid)
    except (OSError, target_guard.RestoreTargetError):
        return True
    return False


def docker_commands(script: str) -> list[str]:
    commands: list[str] = []
    current: list[str] = []
    collecting = False
    for line in script.splitlines():
        if not collecting and re.search(r"\bdocker (?:run|create)\b", line):
            collecting = True
        if not collecting:
            continue
        current.append(line.strip())
        if not line.rstrip().endswith("\\"):
            commands.append(" ".join(current))
            current = []
            collecting = False
    if current:
        raise RuntimeError("unterminated docker command in restore drill")
    return commands


restore_container_commands = docker_commands(restore)
restore_cap_add_commands = [command for command in restore_container_commands if "--cap-add" in command]
restore_init_command = next(
    (command for command in restore_container_commands
     if '-v "$volume:/restore-drill"' in command and "--user 0:0" in command),
    "",
)


def function_body(script: str, name: str, following: str) -> str:
    start = script.index(f"{name}() {{")
    end = script.index(f"{following}() {{", start)
    return script[start:end]


base_body = function_body(entry, "base_backup", "prune_repository_if_due")
base_loop_body = function_body(entry, "base_backup_loop", "wal_stream")
wal_body = function_body(entry, "wal_stream", "verify_auth")
base_service_start = production_compose.index("\n  database-backup:")
base_service_end = production_compose.index("\n  database-wal-archive:", base_service_start)
production_base_service = production_compose[base_service_start:base_service_end]


def disk_staging_contract(script: str) -> bool:
    return all(value in script for value in (
        "SAYDIN_BACKUP_BASE_STAGING_DIR",
        "[ \"$SAYDIN_BACKUP_BASE_STAGING_DIR\" = /var/lib/saydin-backup/base-staging ]",
        'mountpoint -q "$SAYDIN_BACKUP_BASE_STAGING_DIR"',
        'backup_base_staging_disk_required',
        'backup_base_staging_permissions_invalid',
        'backup_base_staging_capacity_insufficient',
        'base_target=$base_staging_root/current',
        'configure_cache "$base_staging_root/restic-cache"',
        'rm -rf -- "$base_target"',
    )) and "/tmp/basebackup" not in script


def immediate_bounded_loop(script: str) -> bool:
    body = function_body(script, "base_backup_loop", "wal_stream")
    freshness = function_body(script, "seconds_until_next_base", "write_wal_recovery_metric")
    return (body.index('remaining=$(seconds_until_next_base)')
            < body.index('if "$0" base-backup')
            and body.index('configure_base_staging reconcile')
            < body.index('remaining=$(seconds_until_next_base)')
            and 'sleep "$remaining"' in body and "sleep 86400" not in body
            and '[ "$status" = 75 ] || exit "$status"' in body
            and "delay=60" in body and "delay=900" in body
            and 'saydin_backup_base.prom' in freshness
            and 'die "backup_base_metric_future" 78' in freshness
            and '[ "$age" -lt 86400 ]' in freshness)


def wal_recovery_contract(script: str) -> bool:
    body = function_body(script, "wal_stream", "verify_auth")
    return all(value in body for value in (
        'watermark_file="$spool/.last-offhost-segment"',
        "--exclude='*.partial'", "write_wal_recovery_metric",
        'SAYDIN_BACKUP_WAL_UPLOAD_INTERVAL_SECONDS" = 300',
        'source_timestamp=$(stat -c %Y "$spool/$newest"',
    )) and (body.index('restic --retry-lock "$restic_retry_lock" backup')
            < body.index('write_wal_recovery_metric "$recovery_timestamp" "$source_timestamp"')
            < body.index('mv "$watermark_temp" "$watermark_file"')) and all(
                value in script for value in (
                    "saydin_backup_wal_last_segment_timestamp_seconds",
                    'die "backup_metrics_directory_missing" 78',
                    'die "backup_wal_recovery_metric_write_failed" 75',
                    '[ "$segment_at" -gt 0 ] && [ "$segment_at" -le "$recovered_at" ]',
                ))


def wal_deferred_upload_contract(script: str) -> bool:
    body = function_body(script, "wal_stream", "verify_auth")
    deferred = body.index('if ! try_acquire_physical_probe_lock; then')
    continuation = body.index('continue', deferred)
    upload = body.index('upload_unverified_wal_snapshot', deferred)
    helper = function_body(script, "upload_unverified_wal_snapshot", "backup_exit")
    return deferred < upload < continuation and all(value in helper for value in (
        "--tag wal-unverified", "--exclude='.saydin-wal-observation'",
        "--exclude='.last-offhost-segment'", "--keep-within 14d",
    )) and "write_wal_recovery_metric" not in helper


def restore_recovery_contract(script: str) -> bool:
    required = (
        "SELECT NOT pg_is_in_recovery();", '[ "$recovery_state" != t ] || break',
        "restore_recovery_target_timeout", '"recoveryTargetReached":True',
    )
    return all(value in script for value in required) and (
        script.index("SELECT NOT pg_is_in_recovery();")
        < script.index("RoleBootstrap.dll verify")
        < script.index('"recoveryTargetReached":True')
    )

required = {
    "client_encryption": (
        "RESTIC_PASSWORD_FILE" in entry
        and 'restic --retry-lock "$restic_retry_lock" backup' in entry
    ),
    "base_manifest": "pg_verifybackup" in entry and "--manifest-checksums=SHA256" in entry,
    "bounded_backup_connections": "--wal-method=fetch" in entry and "--wal-method=stream" not in entry,
    "wal_stream": "pg_receivewal" in entry and "--keep-within 14d" in entry,
    "restore_guard": "saydin-validate-restore-target" in entry and "DISPOSABLE_RESTORE_ONLY" in entry,
    "managed_gates": all(value in restore for value in ("RoleBootstrap.dll verify", "--verify-only", "--hmac-key-file", "restore_api_smoke_failed")),
    "immutable_bases": dockerfile.count("@sha256:") == 2,
    "no_admin_fallback": "PGUSER=saydin_admin" not in entry and "PGUSER=saydin_admin" not in restore,
    "failure_metric_on_exit": "trap 'backup_exit $?' EXIT" in entry and "saydin_backup_last_failure_timestamp_seconds" in entry,
    "time_bounded_restore": "--before" not in entry and "saydin-select-base-snapshot" in entry,
    "deployment_bound_restore": entry.count('--host "$SAYDIN_DEPLOYMENT_ID"') >= 2,
    "restore_kms_fail_closed": all(value in restore for value in (
        "oci-kms-instance-principal", "--kms-key-id", "--kms-key-version-id",
        "--kms-crypto-endpoint", "--evidence-public-key", "egress_network",
    )) and "--evidence-private-key" not in restore,
    "restore_off_host_fetch_has_egress": (
        '--network "$egress_network"' in restore
        and 'docker network create "$egress_network"' in restore
        and '--network "$network" -v "$volume:/restore-drill"' not in restore
    ),
    "daily_base_scheduler": (
        "base_backup_loop" in entry and "seconds_until_next_base" in entry
        and '[ "$age" -lt 86400 ]' in entry
    ),
    "base_disk_staging_guard": disk_staging_contract(entry),
    "base_cleanup_on_all_exit_paths": (
        "cleanup_base_staging" in entry
        and "trap 'backup_exit $?' EXIT" in entry
        and all(f"trap 'exit {code}' {signal}" in entry for code, signal in (
            (129, "HUP"), (130, "INT"), (143, "TERM")))
    ),
    "backup_metrics_preflight": (
        "configure_metrics" in entry
        and 'die "backup_metrics_directory_invalid" 78' in entry
        and 'die "backup_metrics_not_writable" 78' in entry
        and entry.count("configure_metrics") >= 4
    ),
    "base_immediate_bounded_retry": immediate_bounded_loop(entry),
    "base_jobs_are_serialized": (
        'until flock -n 9' in entry and 'lock_wait_remaining=7200' in entry
        and 'backup_base_staging_lock_timeout' in entry
    ),
    "restic_lock_retry_and_weekly_prune": (
        'restic_retry_lock=15m' in entry
        and entry.count('restic --retry-lock "$restic_retry_lock"') >= 6
        and 'restic --no-cache --retry-lock "$restic_retry_lock" prune' in entry
        and "604800" in entry and "forget --tag base" in entry
        and "forget --tag wal" in entry and "forget --prune" not in entry
    ),
    "wal_completed_segment_recovery_metric": wal_recovery_contract(entry),
    "wal_deferred_probe_still_uploads_off_host": wal_deferred_upload_contract(entry),
    "wal_spool_capacity_floor": all(value in entry for value in (
        "configure_wal_spool", "SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES",
        "backup_wal_spool_capacity_insufficient",
        "saydin_backup_wal_spool_free_bytes",
        "saydin_backup_wal_spool_capacity_floor_bytes",
    )) and all(value in production_compose for value in (
        "SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES",
        "backup_wal_spool:/work/wal",
    )),
    "physical_client_wall_clock_bounds": (
        entry.count("PGCONNECT_TIMEOUT=10") == 1
        and "timeout -s TERM -k 5 30" in entry
        and "timeout -s TERM -k 30 7200" in entry
        and production_compose.count('PGCONNECT_TIMEOUT: "10"') == 2
    ),
    "wal_probe_failure_becomes_alertable": all(value in entry for value in (
        "record_wal_probe_failure", 'wal_probe_failure_count=0',
        '[ "$wal_probe_failure_count" -ge 3 ]', "write_failure_metric wal",
    )),
    "wal_spool_atomic_temp_cleanup": all(value in entry for value in (
        'observation_temp="$spool/.saydin-wal-observation.tmp"',
        'watermark_temp="$spool/.last-offhost-segment.tmp"',
        'rm -f -- "$observation_temp" "$watermark_temp"',
        "--exclude='.saydin-wal-observation.tmp'",
        "--exclude='.last-offhost-segment.tmp'",
    )) and ".$$\"" not in "\n".join(
        line for line in wal_body.splitlines()
        if ".saydin-wal-observation" in line or ".last-offhost-segment" in line),
    "postgres_segment_rotation_bound": "archive_timeout=300s" in production_compose,
    "archive_timeout_receiver_smoke_contract": all(value in archive_timeout_smoke.read_text(
        encoding="utf-8") for value in (
            "PostgreSQL init process complete; ready for start up.",
            '"archive_timeout=30s"', '"checkpoint_timeout=30s"',
            'settings != "30s|30s|off|16MB"', '"--synchronous"',
            'target not in partials(receiver)', 'size != 16 * 1024 * 1024',
            'archive_timeout_cleanup_residual', 'residual=0',
        )),
    "production_base_staging_volume": all(value in production_compose for value in (
        "backup_base_staging:/var/lib/saydin-backup/base-staging",
        "SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES",
        "size=64m",
    )) and "size=2g" not in production_base_service,
    "bounded_physical_slot": "--create-slot --if-not-exists" in entry and '--slot="$slot"' in entry,
    "restore_containers_drop_all_capabilities": (
        bool(restore_container_commands)
        and all("--cap-drop ALL" in command for command in restore_container_commands)
    ),
    "restore_init_only_adds_chown": (
        len(restore_cap_add_commands) == 1
        and restore_cap_add_commands[0] == restore_init_command
        and "--cap-add CHOWN" in restore_init_command
    ),
    "restore_init_private_mode_before_chown": (
        "-c 'chmod 0700 /restore-drill && chown 1001:1001 /restore-drill'"
        in restore_init_command
    ),
    "locale_independent_backup_auth": (
        "password authentication failed" not in entry.lower()
        and "pg_hba.conf rejects connection" not in entry.lower()
        and "password authentication failed" not in backup_auth_acceptance.lower()
        and "pg_hba.conf rejects connection" not in backup_auth_acceptance.lower()
        and entry.index("verify_physical_authentication") < entry.index("backup_sql_access_allowed")
    ),
    "restore_cwd_independent_helper": (
        'repo_root=$(CDPATH=' in restore
        and '"$repo_root/infrastructure/release/release_manifest.py"' in restore
    ),
    "restore_signal_and_residual_gate": all(value in restore for value in (
        "trap 'exit 129' HUP", "trap 'exit 130' INT", "trap 'exit 143' TERM",
        "restore_cleanup_residual", "resources_admitted=true", "docker_reachable",
        'remove_owned_resource volume "$volume"', "restore_resource_preexists",
    )),
    "restore_attempt_scoped_dqa": all(value in restore for value in (
        "run_attempt=$2", 'prefix_name="saydin-restore-$run_id-$run_attempt"',
        '"/run/output/$run_id-$run_attempt/evidence"', "restore_audit_run_exists",
    )),
    "restore_completed_wal_rpo": all(value in restore for value in (
        "wal-recovery-evidence.json", "guaranteedRecoveryPointAt",
        "currentRecoveryPointAgeSeconds", "recoveryTargetReached",
        "SELECT NOT pg_is_in_recovery()", "restore_recovery_target_timeout",
    )) and "restore_rpo_exceeded" not in restore and restore_recovery_contract(restore),
    "restic_wal_snapshot_intersection": (
        'snapshots --tag wal,wal-observation' in entry
        and '/work/wal/.saydin-wal-observation' in entry
        and '/work/wal-spool/.saydin-wal-observation' not in entry
    ),
}

# Mutation checks are intentionally tied to the former production failures, rather
# than merely proving that the positive tokens exist.
required["mutation_tmpfs_base_rejected"] = not disk_staging_contract(
    entry.replace('base_target=$base_staging_root/current', 'base_target=/tmp/basebackup'))
required["mutation_sleep_first_rejected"] = not immediate_bounded_loop(
    entry.replace('if "$0" base-backup; then', 'sleep 86400\n    if "$0" base-backup; then', 1))
required["mutation_unconditional_wal_metric_rejected"] = not wal_recovery_contract(
    entry.replace('write_wal_recovery_metric "$recovery_timestamp" "$source_timestamp"',
                  'mv "$watermark_temp" "$watermark_file"\n      '
                  'write_wal_recovery_metric "$recovery_timestamp" "$source_timestamp"', 1))
required["mutation_recovery_target_state_rejected"] = not restore_recovery_contract(
    restore.replace("SELECT NOT pg_is_in_recovery();", "SELECT true;", 1))
required["mutation_deferred_wal_upload_rejected"] = not wal_deferred_upload_contract(
    entry.replace("upload_unverified_wal_snapshot || die", ": || die", 1))
required["docker_smokes_never_vacuously_pass"] = all(
    "return 77" in path.read_text(encoding="utf-8")
    for path in (restic_wal_smoke, archive_timeout_smoke, base_backup_smoke,
                 volume_init_smoke)
)
required["entrypoint_shell_syntax"] = subprocess.run(
    ["/bin/sh", "-n", str(ROOT / "infrastructure/backup/backup-entrypoint.sh")],
    check=False, capture_output=True, text=True).returncode == 0
required["restore_shell_syntax"] = subprocess.run(
    ["/bin/sh", "-n", str(ROOT / "infrastructure/backup/restore-drill.sh")],
    check=False, capture_output=True, text=True).returncode == 0
required["wal_recovery_evidence_behavior"] = subprocess.run(
    [sys.executable, str(wal_evidence_smoke)], check=False,
    capture_output=True, text=True).returncode == 0
highwater = subprocess.run(
    [sys.executable, str(wal_highwater_path), "1", "1/00000000", "16MB"],
    check=False, capture_output=True, text=True)
required["wal_highwater_conversion"] = (
    highwater.returncode == 0
    and highwater.stdout.strip() == "000000010000000100000000|0000000100000000000000FF"
    and "IDENTIFY_SYSTEM" in wal_body
    and "backup_wal_receiver_not_caught_up" in wal_body
    and base_body.index("acquire_physical_probe_lock")
        < base_body.index("verify_physical_authentication")
        < base_body.index("pg_basebackup")
        < base_body.index("release_physical_probe_lock")
)
restic_wal_result = subprocess.run(
    [sys.executable, str(restic_wal_smoke)], check=False,
    capture_output=True, text=True)
record_smoke(
    required, "restic_wal_observation_behavior", restic_wal_result,
    docker_bound=True)
required["restore_cleanup_behavior"] = subprocess.run(
    [sys.executable, str(restore_cleanup_smoke)], check=False,
    capture_output=True, text=True).returncode == 0
archive_timeout_result = subprocess.run(
    [sys.executable, str(archive_timeout_smoke)], check=False,
    capture_output=True, text=True)
record_smoke(
    required, "archive_timeout_receiver_behavior", archive_timeout_result,
    docker_bound=True)
with tempfile.TemporaryDirectory(prefix="saydin-backup-selector-") as raw:
    inventory = Path(raw) / "snapshots.json"
    inventory.write_text(json.dumps([
        {"id": "a" * 64, "time": "2026-08-18T23:59:59.999999999Z"},
        {"id": "b" * 64, "time": "2026-08-19T00:00:01.000000000Z"},
    ]), encoding="utf-8")
    selected = subprocess.run(
        [sys.executable, str(selector), str(inventory), "2026-08-19T00:00:00Z"],
        capture_output=True, text=True, check=False,
    )
    required["nanosecond_target_selection"] = selected.returncode == 0 and selected.stdout.strip() == "a" * 64
with tempfile.TemporaryDirectory(prefix="saydin-restore-target-") as raw:
    temp = Path(raw)

    valid_root = temp / "valid"
    valid_root.mkdir(mode=0o700)
    prepared = target_guard.prepare_restore_target(valid_root, str(valid_root / "work"))
    required["restore_target_openat_positive"] = (
        prepared == valid_root / "work"
        and all(path.is_dir() and not path.is_symlink() for path in (
            prepared, prepared / "base", prepared / "wal"))
        and all((path.stat().st_mode & 0o777) == 0o700 for path in (
            valid_root, prepared, prepared / "base", prepared / "wal"))
    )

    traversal_root = temp / "traversal"; traversal_root.mkdir(mode=0o700)
    required["restore_target_traversal_rejected"] = guard_rejects(
        traversal_root, str(traversal_root / "nested" / ".." / "work"))
    broad_root = temp / "broad"; broad_root.mkdir(mode=0o700)
    required["restore_target_root_rejected"] = guard_rejects(broad_root, str(broad_root))
    other_root = temp / "other"; other_root.mkdir(mode=0o700)
    required["restore_target_other_leaf_rejected"] = guard_rejects(
        other_root, str(other_root / "arbitrary"))

    actual_root = temp / "actual"; actual_root.mkdir(mode=0o700)
    symlink_root = temp / "root-link"; symlink_root.symlink_to(actual_root, target_is_directory=True)
    required["restore_target_symlink_root_rejected"] = guard_rejects(
        symlink_root, str(symlink_root / "work"))

    nonempty_root = temp / "nonempty"; nonempty_root.mkdir(mode=0o700)
    (nonempty_root / "sentinel").write_text("preserve", encoding="utf-8")
    required["restore_target_nonempty_root_rejected"] = guard_rejects(
        nonempty_root, str(nonempty_root / "work"))

    leaf_link_root = temp / "leaf-link"; leaf_link_root.mkdir(mode=0o700)
    (leaf_link_root / "work").symlink_to(temp, target_is_directory=True)
    required["restore_target_symlink_leaf_rejected"] = guard_rejects(
        leaf_link_root, str(leaf_link_root / "work"))

    mode_root = temp / "mode"; mode_root.mkdir(mode=0o700); mode_root.chmod(0o755)
    required["restore_target_private_mode_required"] = guard_rejects(
        mode_root, str(mode_root / "work"))

    owner_root = temp / "owner"; owner_root.mkdir(mode=0o700)
    required["restore_target_process_owner_required"] = guard_rejects(
        owner_root, str(owner_root / "work"), expected_uid=os.geteuid() + 1)
volume_init_result = subprocess.run(
    [sys.executable, str(volume_init_smoke)],
    capture_output=True, text=True, check=False,
)
record_smoke(
    required, "restore_volume_init_docker_smoke", volume_init_result,
    docker_bound=True)
base_smoke_result = subprocess.run(
    [sys.executable, str(base_backup_smoke)],
    capture_output=True, text=True, check=False,
)
record_smoke(
    required, "base_backup_docker_behavior_smoke", base_smoke_result,
    docker_bound=True)
if len(required) != EXPECTED_CHECK_COUNT:
    print(
        f"backup_static_failed:check_count:{len(required)}:{EXPECTED_CHECK_COUNT}",
        file=sys.stderr,
    )
    raise SystemExit(2)
failed = [name for name, passed in required.items() if not passed]
if failed:
    print("backup_static_failed:" + ",".join(failed), file=sys.stderr)
    raise SystemExit(2)
suffix = f" skipped:{','.join(skipped)}" if skipped else ""
print(f"backup_static_self_test_passed:{len(required)}{suffix}")
