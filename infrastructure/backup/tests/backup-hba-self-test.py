#!/usr/bin/env python3
from __future__ import annotations

import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[3]
TOOL = ROOT / "infrastructure/backup/manage_backup_hba.py"
PREFIX = "saydin_ci__0123456789abcdef01234567"


def run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(TOOL), *args],
        check=False,
        capture_output=True,
        text=True,
    )


with tempfile.TemporaryDirectory(prefix="saydin-hba-test-") as raw:
    root = Path(raw).resolve()
    hba = root / "pg_hba.conf"
    hba.write_text(
        "local all all trust\n"
        "host all all 127.0.0.1/32 scram-sha-256\n"
        "host all all 0.0.0.0/0 scram-sha-256\n",
        encoding="utf-8",
    )
    hba.chmod(0o600)
    base = ("--hba", str(hba), "--cidr", "172.28.0.0/16", "--role-prefix", PREFIX, "--fixture-cleartext")

    installed = run("install", *base)
    assert installed.returncode == 0, installed.stderr
    first = hba.read_bytes()
    assert stat.S_IMODE(hba.stat().st_mode) == 0o600
    assert first.index(b"host replication") < first.index(b"host all all 127.0.0.1/32")
    assert b"host all " + f"{PREFIX}_backup_login_v1".encode() + b" 0.0.0.0/0 reject\n" in first
    assert b"host all " + f"{PREFIX}_backup_login_v2".encode() + b" ::0/0 reject\n" in first

    verified = run("verify", *base)
    assert verified.returncode == 0, verified.stderr
    repeated = run("install", *base)
    assert repeated.returncode == 0 and hba.read_bytes() == first

    production_broad = run(
        "verify", "--hba", str(hba), "--cidr", "172.28.0.0/16", "--role-prefix", PREFIX
    )
    assert production_broad.returncode == 78
    assert production_broad.stderr.strip() == "backup_hba_dedicated_cidr_scope_invalid"

    hba.write_text(hba.read_text() + f"host replication {PREFIX}_backup_login_v1 10.0.0.0/8 trust\n")
    outside = run("verify", *base)
    assert outside.returncode == 78
    assert outside.stderr.strip() == "backup_hba_rule_outside_managed_block"

    hba.write_bytes(first.replace(b"scram-sha-256", b"trust", 1))
    tampered = run("verify", *base)
    assert tampered.returncode == 78
    assert tampered.stderr.strip() == "backup_hba_managed_block_mismatch"

    symlink = root / "linked-hba"
    symlink.symlink_to(hba)
    linked = run("verify", "--hba", str(symlink), *base[2:])
    assert linked.returncode == 78
    assert linked.stderr.strip() == "backup_hba_regular_file_required"

    hardlink = root / "hardlinked-hba"
    os.link(hba, hardlink)
    hardlinked = run("verify", "--hba", str(hardlink), *base[2:])
    assert hardlinked.returncode == 78
    assert hardlinked.stderr.strip() == "backup_hba_link_count_invalid"

print("backup_hba_self_test_passed:8")
