#!/usr/bin/env python3
"""Atomically prepare the one permitted disposable restore leaf below its private root."""

from __future__ import annotations

import os
import stat
import sys
from pathlib import Path


class RestoreTargetError(ValueError):
    pass


def _owned_private_directory(file_descriptor: int, expected_uid: int, code: str) -> None:
    metadata = os.fstat(file_descriptor)
    if (not stat.S_ISDIR(metadata.st_mode)
            or metadata.st_uid != expected_uid
            or stat.S_IMODE(metadata.st_mode) != 0o700):
        raise RestoreTargetError(code)


def prepare_restore_target(
    root: Path,
    target_text: str,
    *,
    expected_uid: int | None = None,
) -> Path:
    """Create `work/base` and `work/wal` without following an existing path component."""
    uid = os.geteuid() if expected_uid is None else expected_uid
    expected_target = root / "work"
    if target_text != str(expected_target):
        raise RestoreTargetError("restore_target_not_exact_leaf")

    root_metadata = os.lstat(root)
    if stat.S_ISLNK(root_metadata.st_mode) or not stat.S_ISDIR(root_metadata.st_mode):
        raise RestoreTargetError("restore_root_invalid")
    if root_metadata.st_uid != uid or stat.S_IMODE(root_metadata.st_mode) != 0o700:
        raise RestoreTargetError("restore_root_private_owner_invalid")

    flags = os.O_RDONLY | os.O_DIRECTORY
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    root_fd = os.open(root, flags)
    try:
        _owned_private_directory(root_fd, uid, "restore_root_private_owner_invalid")
        if os.listdir(root_fd):
            raise RestoreTargetError("restore_root_not_empty")
        os.mkdir("work", mode=0o700, dir_fd=root_fd)
        work_fd = os.open("work", flags, dir_fd=root_fd)
        try:
            _owned_private_directory(work_fd, uid, "restore_target_private_owner_invalid")
            os.mkdir("base", mode=0o700, dir_fd=work_fd)
            os.mkdir("wal", mode=0o700, dir_fd=work_fd)
            for name in ("base", "wal"):
                leaf_fd = os.open(name, flags, dir_fd=work_fd)
                try:
                    _owned_private_directory(
                        leaf_fd, uid, "restore_target_private_owner_invalid")
                finally:
                    os.close(leaf_fd)
        finally:
            os.close(work_fd)
    finally:
        os.close(root_fd)
    return expected_target


def main() -> int:
    if len(sys.argv) != 2:
        print("restore_target_guard_usage", file=sys.stderr)
        return 64
    try:
        target = prepare_restore_target(Path("/restore-drill"), sys.argv[1])
    except (OSError, RestoreTargetError) as error:
        code = str(error) if isinstance(error, RestoreTargetError) else "restore_target_filesystem_invalid"
        print(code, file=sys.stderr)
        return 78
    print(target)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
