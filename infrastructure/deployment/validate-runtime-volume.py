#!/usr/bin/env python3
"""Validate one pre-provisioned writable runtime volume without mutating it."""

from __future__ import annotations

import argparse
import os
import stat
import sys
from pathlib import Path


def validate(root: Path, expected_uid: int, expected_mode: int = 0o700) -> str | None:
    try:
        value = os.lstat(root)
        if stat.S_ISLNK(value.st_mode) or not stat.S_ISDIR(value.st_mode):
            return "directory_type"
        if value.st_uid != expected_uid or stat.S_IMODE(value.st_mode) != expected_mode:
            return "directory_owner_or_mode"
        if value.st_nlink < 2:
            return "directory_link_count"
    except OSError:
        return "directory_io"
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--uid", type=int, required=True)
    parser.add_argument("--mode", default="0700")
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    try:
        mode = int(args.mode, 8)
    except ValueError:
        print("runtime_volume_rejected:mode_invalid", file=sys.stderr)
        return 64
    if not args.root.is_absolute() or args.uid < 0 or not 0 <= mode <= 0o777:
        print("runtime_volume_rejected:argument_invalid", file=sys.stderr)
        return 64
    error = validate(args.root, args.uid, mode)
    if error:
        print(f"runtime_volume_rejected:{error}", file=sys.stderr)
        return 78
    print("runtime_volume_accepted")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
