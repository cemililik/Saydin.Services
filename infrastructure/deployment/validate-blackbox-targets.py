#!/usr/bin/env python3
"""Validate the single allowlisted external blackbox target from a private volume."""

from __future__ import annotations

import argparse
import json
import os
import re
import stat
import sys
from pathlib import Path


HOST = re.compile(r"^[a-z0-9][a-z0-9.-]*[a-z0-9]$")


def validate(root: Path, public_host: str, expected_uid: int = 65534) -> bool:
    expected_url = f"https://{public_host}/health/live"
    try:
        if not HOST.fullmatch(public_host) or "." not in public_host:
            raise ValueError("host")
        root_stat = os.lstat(root)
        if (not root.is_absolute() or stat.S_ISLNK(root_stat.st_mode)
                or not stat.S_ISDIR(root_stat.st_mode) or root_stat.st_uid != expected_uid
                or stat.S_IMODE(root_stat.st_mode) != 0o700):
            raise ValueError("root")
        entries = list(root.iterdir())
        if [item.name for item in entries] != ["blackbox.json"]:
            raise ValueError("file_set")
        path = entries[0]
        value_stat = os.lstat(path)
        if (stat.S_ISLNK(value_stat.st_mode) or not stat.S_ISREG(value_stat.st_mode)
                or value_stat.st_uid != expected_uid or value_stat.st_nlink != 1
                or stat.S_IMODE(value_stat.st_mode) not in {0o400, 0o600}
                or value_stat.st_size > 16_384):
            raise ValueError("file_metadata")
        value = json.loads(path.read_text(encoding="utf-8"))
        if value != [{"targets": [expected_url], "labels": {"service": "saydin-edge"}}]:
            raise ValueError("target_contract")
    except (OSError, ValueError):
        return False
    return True


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--public-host", required=True)
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    if not validate(args.root, args.public_host):
        print("blackbox_targets_rejected", file=sys.stderr)
        return 78
    print("blackbox_targets_accepted")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
