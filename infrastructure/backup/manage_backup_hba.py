#!/usr/bin/env python3
"""Install or verify Saydin's exact physical-backup pg_hba.conf boundary."""

from __future__ import annotations

import argparse
import ipaddress
import os
from pathlib import Path
import re
import stat
import sys
import tempfile


BEGIN = "# SAYDIN MANAGED BACKUP HBA BEGIN v1"
END = "# SAYDIN MANAGED BACKUP HBA END v1"
PREFIX = re.compile(r"^saydin_[a-z][a-z0-9_]{2}_[0-9a-f]{24}$")
BACKUP_ROLE = re.compile(r"\bsaydin_[a-z][a-z0-9_]{2}_[0-9a-f]{24}_backup_login_v[12]\b")
HOST_RULE = re.compile(r"^\s*host(?:ssl|nossl)?\s+")


class ContractError(RuntimeError):
    pass


def exact_path(value: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        raise ContractError("backup_hba_absolute_path_required")
    try:
        before = os.lstat(path)
    except OSError as error:
        raise ContractError("backup_hba_file_unavailable") from error
    if stat.S_ISLNK(before.st_mode) or not stat.S_ISREG(before.st_mode):
        raise ContractError("backup_hba_regular_file_required")
    if before.st_nlink != 1:
        raise ContractError("backup_hba_link_count_invalid")
    if before.st_uid != os.geteuid():
        raise ContractError("backup_hba_owner_invalid")
    if path.resolve(strict=True) != path:
        raise ContractError("backup_hba_canonical_path_required")
    return path


def exact_cidr(value: str, allow_fixture: bool) -> str:
    try:
        network = ipaddress.ip_network(value, strict=True)
    except ValueError as error:
        raise ContractError("backup_hba_cidr_invalid") from error
    if network.version != 4 or not network.is_private:
        raise ContractError("backup_hba_private_ipv4_cidr_required")
    if allow_fixture:
        if network.prefixlen < 16 or network.prefixlen > 28:
            raise ContractError("backup_hba_fixture_cidr_scope_invalid")
    elif network.prefixlen < 28 or network.prefixlen > 30:
        raise ContractError("backup_hba_dedicated_cidr_scope_invalid")
    if str(network) != value:
        raise ContractError("backup_hba_canonical_cidr_required")
    return value


def exact_prefixes(values: list[str]) -> list[str]:
    if not values or len(values) > 16:
        raise ContractError("backup_hba_role_prefix_count_invalid")
    if any(not PREFIX.fullmatch(value) for value in values):
        raise ContractError("backup_hba_role_prefix_invalid")
    if len(set(values)) != len(values):
        raise ContractError("backup_hba_role_prefix_duplicate")
    return sorted(values)


def contract_lines(prefixes: list[str], cidr: str, rule_kind: str) -> list[str]:
    result = [BEGIN]
    for prefix in prefixes:
        for version in ("v1", "v2"):
            role = f"{prefix}_backup_login_{version}"
            result.extend(
                (
                    f"{rule_kind} replication {role} {cidr} scram-sha-256",
                    f"host all {role} 0.0.0.0/0 reject",
                    f"host all {role} ::0/0 reject",
                )
            )
    result.append(END)
    return result


def block_bounds(lines: list[str]) -> tuple[int, int] | None:
    begin = [index for index, line in enumerate(lines) if line == BEGIN]
    end = [index for index, line in enumerate(lines) if line == END]
    if not begin and not end:
        return None
    if len(begin) != 1 or len(end) != 1 or begin[0] >= end[0]:
        raise ContractError("backup_hba_managed_markers_invalid")
    return begin[0], end[0]


def first_host_rule(lines: list[str], ignored: tuple[int, int] | None = None) -> int:
    for index, line in enumerate(lines):
        if ignored is not None and ignored[0] <= index <= ignored[1]:
            continue
        stripped = line.lstrip()
        if stripped.startswith("#") or not stripped:
            continue
        if HOST_RULE.match(line):
            return index
    raise ContractError("backup_hba_generic_host_rule_missing")


def reject_outside_backup_rules(lines: list[str], bounds: tuple[int, int] | None) -> None:
    for index, line in enumerate(lines):
        if bounds is not None and bounds[0] <= index <= bounds[1]:
            continue
        if BACKUP_ROLE.search(line):
            raise ContractError("backup_hba_rule_outside_managed_block")


def verify(lines: list[str], expected: list[str]) -> None:
    bounds = block_bounds(lines)
    if bounds is None:
        raise ContractError("backup_hba_managed_block_missing")
    reject_outside_backup_rules(lines, bounds)
    actual = lines[bounds[0] : bounds[1] + 1]
    if actual != expected:
        raise ContractError("backup_hba_managed_block_mismatch")
    if bounds[0] >= first_host_rule(lines, bounds):
        raise ContractError("backup_hba_managed_block_order_invalid")


def install(path: Path, original: os.stat_result, lines: list[str]) -> None:
    parent_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    temp_path: str | None = None
    try:
        fd, temp_path = tempfile.mkstemp(prefix=".saydin-pg-hba.", dir=path.parent)
        try:
            os.fchmod(fd, stat.S_IMODE(original.st_mode))
            os.fchown(fd, original.st_uid, original.st_gid)
            payload = ("\n".join(lines) + "\n").encode("utf-8")
            with os.fdopen(fd, "wb", closefd=True) as stream:
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temp_path, path)
            temp_path = None
            os.fsync(parent_fd)
        finally:
            if temp_path is not None:
                try:
                    os.unlink(temp_path)
                except FileNotFoundError:
                    pass
    finally:
        os.close(parent_fd)


def main() -> int:
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("action", choices=("install", "verify"))
    parser.add_argument("--hba", required=True)
    parser.add_argument("--cidr", required=True)
    parser.add_argument("--role-prefix", action="append", required=True)
    parser.add_argument("--fixture-cleartext", action="store_true")
    args = parser.parse_args()

    try:
        path = exact_path(args.hba)
        cidr = exact_cidr(args.cidr, args.fixture_cleartext)
        prefixes = exact_prefixes(args.role_prefix)
        rule_kind = "host" if args.fixture_cleartext else "hostssl"
        expected = contract_lines(prefixes, cidr, rule_kind)
        original = os.stat(path, follow_symlinks=False)
        text = path.read_text(encoding="utf-8")
        if "\x00" in text:
            raise ContractError("backup_hba_nul_forbidden")
        lines = text.splitlines()
        bounds = block_bounds(lines)
        reject_outside_backup_rules(lines, bounds)

        if args.action == "install":
            if bounds is not None:
                del lines[bounds[0] : bounds[1] + 1]
            insertion = first_host_rule(lines)
            updated = lines[:insertion] + expected + lines[insertion:]
            install(path, original, updated)
            lines = path.read_text(encoding="utf-8").splitlines()

        verify(lines, expected)
        print("backup_hba_contract_ok")
        return 0
    except (ContractError, OSError, UnicodeError) as error:
        code = str(error) if isinstance(error, ContractError) else "backup_hba_io_error"
        print(code, file=sys.stderr)
        return 78


if __name__ == "__main__":
    raise SystemExit(main())
