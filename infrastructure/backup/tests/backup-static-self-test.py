#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import importlib.util
import json
import os
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[3]
entry = (ROOT / "infrastructure/backup/backup-entrypoint.sh").read_text(encoding="utf-8")
restore = (ROOT / "infrastructure/backup/restore-drill.sh").read_text(encoding="utf-8")
dockerfile = (ROOT / "infrastructure/backup/Dockerfile").read_text(encoding="utf-8")
selector = ROOT / "infrastructure/backup/select-base-snapshot.py"
target_guard_path = ROOT / "infrastructure/backup/restore_target_guard.py"


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

required = {
    "client_encryption": "RESTIC_PASSWORD_FILE" in entry and "restic backup" in entry,
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
    "daily_base_scheduler": "base_backup_loop" in entry and "sleep 86400" in entry,
    "bounded_physical_slot": "--create-slot --if-not-exists" in entry and '--slot="$slot"' in entry,
}
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
failed = [name for name, passed in required.items() if not passed]
if failed:
    print("backup_static_failed:" + ",".join(failed), file=sys.stderr)
    raise SystemExit(2)
print(f"backup_static_self_test_passed:{len(required)}")
