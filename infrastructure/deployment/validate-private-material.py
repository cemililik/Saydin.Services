#!/usr/bin/env python3
"""Validate pre-materialized production secrets without printing names or values."""

from __future__ import annotations

import argparse
import json
import os
import re
import stat
import sys
from pathlib import Path


EXPECTED: dict[str, tuple[int, dict[str, str]]] = {
    "postgres": (70, {
        "password": "scalar", "server.crt": "certificate-pem", "server.key": "private-pem",
    }),
    "redis": (999, {"redis.conf": "redis"}),
    "bootstrap": (1001, {
        "admin-connection": "bootstrap-admin", "migrator-v1": "scalar", "api-v1": "scalar",
        "ingestion-v1": "scalar", "calendar_importer-v1": "scalar",
        "exporter-v1": "scalar", "audit-v1": "scalar", "backup-v1": "scalar",
    }),
    "migrator": (1001, {"password": "scalar"}),
    "api": (1001, {
        "password": "scalar", "installation-keyring.json": "json",
        "security-limiter-hmac": "scalar",
    }),
    "api-config": (1001, {"appsettings.Production.json": "api-config"}),
    "ingestion": (1001, {"password": "scalar"}),
    "ingestion-config": (1001, {"appsettings.Production.json": "ingestion-config"}),
    "calendar": (1001, {"password": "scalar"}),
    "exporter": (65534, {"password": "scalar"}),
    "redis-exporter": (59000, {"password": "scalar"}),
    "alertmanager": (65534, {"alertmanager.yml": "alertmanager"}),
    "audit": (1001, {
        "password": "scalar", "evidence-public.pem": "public-pem", "evidence-hmac": "scalar",
    }),
    "backup": (1001, {
        "password": "scalar", "repository-password": "scalar",
        "object-store-token": "scalar",
    }),
}
PLACEHOLDER = re.compile(r"change[_-]?me|example\.invalid|placeholder|replace[_-]?me", re.I)


def fail(code: str) -> int:
    print(f"private_material_rejected:{code}", file=sys.stderr)
    return 78


def read_bounded(path: Path, limit: int = 1_048_576) -> bytes:
    with path.open("rb") as stream:
        value = stream.read(limit + 1)
    if len(value) > limit:
        raise ValueError("file_too_large")
    return value


def validate_content(kind: str, content: bytes) -> None:
    text = content.decode("utf-8")
    if PLACEHOLDER.search(text) or "\x00" in text:
        raise ValueError("placeholder_or_binary")
    if kind == "scalar":
        if not 24 <= len(content) <= 4096 or b"\n" in content or b"\r" in content:
            raise ValueError("scalar_shape")
    elif kind == "bootstrap-admin":
        if b"\n" in content or b"\r" in content or len(content) > 4096:
            raise ValueError("admin_connection_shape")
        pairs: dict[str, str] = {}
        for item in text.split(";"):
            key, separator, value = item.partition("=")
            if not separator or key in pairs or not value:
                raise ValueError("admin_connection_shape")
            pairs[key] = value
        if (set(pairs) != {"Host", "Port", "Database", "Username", "Password", "SSL Mode"}
                or pairs["Host"] != "postgres-backup" or pairs["Port"] != "5432"
                or pairs["Username"] != "saydin_admin" or pairs["SSL Mode"] != "Require"
                or not re.fullmatch(r"[a-z][a-z0-9_]{2,62}", pairs["Database"])
                or len(pairs["Password"]) < 24 or any(character.isspace() for character in pairs["Password"])):
            raise ValueError("admin_connection_shape")
    elif kind == "redis":
        lines = [line.strip() for line in text.splitlines() if line.strip()]
        if len(lines) not in {4, 5} or not lines[0].startswith("requirepass "):
            raise ValueError("redis_shape")
        password = lines[0].split(" ", 1)[1]
        if len(password) < 24 or any(character.isspace() for character in password):
            raise ValueError("redis_shape")
        if "appendonly yes" not in lines:
            raise ValueError("redis_shape")
        if not any(re.fullmatch(r"maxmemory [1-9][0-9]*(?:mb|gb)", line, re.I) for line in lines):
            raise ValueError("redis_shape")
        if "maxmemory-policy noeviction" not in lines:
            raise ValueError("redis_shape")
    elif kind in {"json", "api-config", "ingestion-config"}:
        value = json.loads(text)
        if not isinstance(value, dict):
            raise ValueError("json_shape")
        if kind == "json":
            if not 1 <= len(value) <= 2 or any(
                not str(key).isdigit() or not isinstance(item, str) or len(item) < 40
                for key, item in value.items()
            ):
                raise ValueError("json_shape")
        if kind == "api-config":
            if (set(value) != {"ConnectionStrings"}
                    or not isinstance(value["ConnectionStrings"], dict)
                    or set(value["ConnectionStrings"]) != {"Redis"}):
                raise ValueError("api_config_shape")
            redis = value["ConnectionStrings"].get("Redis")
            if not isinstance(redis, str) or "password=" not in redis.lower():
                raise ValueError("api_config_shape")
        if kind == "ingestion-config":
            if set(value) != {"ExternalApis", "IngestionWorkers"} or not isinstance(value["ExternalApis"], dict):
                raise ValueError("ingestion_config_shape")
            workers = value.get("IngestionWorkers")
            if not isinstance(workers, dict) or not any(
                isinstance(item, dict) and item.get("Enabled") is True for item in workers.values()
            ):
                raise ValueError("ingestion_config_shape")
    elif kind == "private-pem":
        if not text.startswith("-----BEGIN PRIVATE KEY-----\n") or "-----BEGIN PUBLIC KEY-----" in text:
            raise ValueError("pem_shape")
    elif kind == "public-pem":
        if not text.startswith("-----BEGIN PUBLIC KEY-----\n") or "PRIVATE KEY" in text:
            raise ValueError("pem_shape")
    elif kind == "certificate-pem":
        if not text.startswith("-----BEGIN CERTIFICATE-----\n") or "PRIVATE KEY" in text:
            raise ValueError("pem_shape")
    elif kind == "alertmanager":
        if "CHANGE_ME" in text or "example.invalid" in text:
            raise ValueError("alertmanager_shape")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("purpose", choices=sorted(EXPECTED))
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    expected_uid, expected_files = EXPECTED[args.purpose]
    try:
        if not args.root.is_absolute():
            return fail("directory_not_absolute")
        unresolved = args.root
        root_stat = os.lstat(unresolved)
        if stat.S_ISLNK(root_stat.st_mode):
            return fail("directory_type")
        for parent in unresolved.parents:
            if parent == parent.parent:
                break
            if parent.exists() and stat.S_ISLNK(os.lstat(parent).st_mode):
                return fail("ancestor_symlink")
        root = unresolved.resolve(strict=True)
        root_stat = os.lstat(root)
        if not stat.S_ISDIR(root_stat.st_mode) or stat.S_ISLNK(root_stat.st_mode):
            return fail("directory_type")
        if root_stat.st_uid != expected_uid or stat.S_IMODE(root_stat.st_mode) != 0o700:
            return fail("directory_owner_or_mode")
        names = {item.name for item in root.iterdir()}
        if names != set(expected_files):
            return fail("file_set")
        for name, kind in expected_files.items():
            path = root / name
            value_stat = os.lstat(path)
            if not stat.S_ISREG(value_stat.st_mode) or stat.S_ISLNK(value_stat.st_mode):
                return fail("file_type")
            if value_stat.st_nlink != 1 or value_stat.st_uid != expected_uid:
                return fail("file_owner_or_link")
            if stat.S_IMODE(value_stat.st_mode) not in {0o400, 0o600}:
                return fail("file_mode")
            validate_content(kind, read_bounded(path))
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError):
        return fail("content_or_io")
    print(f"private_material_accepted:{args.purpose}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
