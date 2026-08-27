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
from urllib.parse import urlsplit


# Content-shape tags for each expected secret file. These name the *format* the
# validator enforces (see validate_content), never a credential value; binding them
# to constants keeps `"<file>": <FORMAT>` entries from reading as inline secrets.
SCALAR = "scalar"
BINARY_32 = "binary-32"
JSON_DOC = "json"
BOOTSTRAP_ADMIN = "bootstrap-admin"
REDIS_CONF = "redis"
ALERTMANAGER = "alertmanager"
API_CONFIG = "api-config"
INGESTION_CONFIG = "ingestion-config"
PRIVATE_PEM = "private-pem"
PUBLIC_PEM = "public-pem"
CERTIFICATE_PEM = "certificate-pem"

EXPECTED: dict[str, tuple[int, dict[str, str]]] = {
    "postgres": (70, {
        "password": SCALAR, "server.crt": CERTIFICATE_PEM, "server.key": PRIVATE_PEM,
    }),
    "redis": (999, {"redis.conf": REDIS_CONF}),
    "bootstrap": (1001, {
        "admin-connection": BOOTSTRAP_ADMIN, "migrator-current": SCALAR,
        "api-current": SCALAR, "ingestion-current": SCALAR,
        "calendar_importer-current": SCALAR, "exporter-current": SCALAR,
        "audit-current": SCALAR, "backup-v1": SCALAR,
    }),
    "migrator": (1001, {"password": SCALAR}),
    "api": (1001, {
        "password": SCALAR, "installation-keyring.json": JSON_DOC,
        "security-limiter-hmac": SCALAR, "activity-principal-hmac": BINARY_32,
    }),
    "api-config": (1001, {"appsettings.Production.json": API_CONFIG}),
    "ingestion": (1001, {"password": SCALAR}),
    "ingestion-config": (1001, {"appsettings.Production.json": INGESTION_CONFIG}),
    "calendar": (1001, {"password": SCALAR}),
    "exporter": (65534, {"password": SCALAR}),
    "redis-exporter": (59000, {"password": SCALAR}),
    "alertmanager": (65534, {"alertmanager.yml": ALERTMANAGER}),
    "audit": (1001, {
        "password": SCALAR, "evidence-public.pem": PUBLIC_PEM, "evidence-hmac": SCALAR,
        "production-target": BINARY_32,
    }),
    "data-repair": (1001, {
        "ingestion-current": SCALAR, "audit-current": SCALAR,
    }),
    "backup": (1001, {
        "password": SCALAR, "repository-password": SCALAR,
        "object-store-token": SCALAR,
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


def _section_blocks(lines: list[str], section: str, item_indent: int) -> list[list[str]]:
    try:
        start = lines.index(section + ":") + 1
    except ValueError as error:
        raise ValueError("yaml_section_missing") from error
    blocks: list[list[str]] = []
    current: list[str] = []
    prefix = " " * item_indent + "- "
    for line in lines[start:]:
        if line and not line.startswith(" "):
            break
        if line.startswith(prefix):
            if current:
                blocks.append(current)
            current = [line]
        elif current:
            current.append(line)
    if current:
        blocks.append(current)
    return blocks


def _receiver_blocks(lines: list[str]) -> dict[str, list[str]]:
    receivers: dict[str, list[str]] = {}
    for block in _section_blocks(lines, "receivers", 2):
        match = re.fullmatch(r"  - name:\s*([a-z0-9-]+)\s*", block[0])
        if match is None or match.group(1) in receivers:
            raise ValueError("alertmanager_receiver_shape")
        receivers[match.group(1)] = block
    return receivers


def _receiver_webhooks(block: list[str]) -> list[tuple[str, bool]]:
    try:
        start = block.index("    webhook_configs:") + 1
    except ValueError as error:
        raise ValueError("alertmanager_webhook_missing") from error
    webhooks: list[tuple[str, bool]] = []
    current: list[str] = []
    for line in block[start:]:
        if line.startswith("      - "):
            if current:
                webhooks.append(_parse_webhook(current))
            current = [line]
        elif current:
            current.append(line)
    if current:
        webhooks.append(_parse_webhook(current))
    if not webhooks:
        raise ValueError("alertmanager_webhook_missing")
    return webhooks


def _parse_webhook(lines: list[str]) -> tuple[str, bool]:
    url_match = re.fullmatch(r"      - url:\s*(\S+)\s*", lines[0])
    if url_match is None:
        raise ValueError("alertmanager_webhook_shape")
    send_resolved = any(
        re.fullmatch(r"        send_resolved:\s*true\s*", line) is not None
        for line in lines[1:]
    )
    return url_match.group(1), send_resolved


def _duration_seconds(value: str) -> float:
    match = re.fullmatch(r"([1-9][0-9]*)(ms|s|m|h)", value)
    if match is None:
        raise ValueError("alertmanager_duration_shape")
    scale = {"ms": 0.001, "s": 1.0, "m": 60.0, "h": 3600.0}[match.group(2)]
    return int(match.group(1)) * scale


def validate_alertmanager(text: str) -> None:
    if "\t" in text:
        raise ValueError("alertmanager_tab_indentation")
    lines = [line.rstrip() for line in text.splitlines()
             if line.strip() and not line.lstrip().startswith("#")]
    try:
        route_start = lines.index("route:")
        routes_start = lines.index("  routes:", route_start + 1)
    except ValueError as error:
        raise ValueError("alertmanager_route_shape") from error
    route_lines = lines[routes_start:]
    route_blocks: list[list[str]] = []
    current: list[str] = []
    for line in route_lines[1:]:
        if line and not line.startswith(" "):
            break
        if line.startswith("    - "):
            if current:
                route_blocks.append(current)
            current = [line]
        elif current:
            current.append(line)
    if current:
        route_blocks.append(current)
    watchdog_routes = [
        block for block in route_blocks
        if any('severity="watchdog"' in line for line in block)
    ]
    if len(watchdog_routes) != 1:
        raise ValueError("alertmanager_watchdog_route")
    watchdog_route = watchdog_routes[0]
    receiver = next((match.group(1) for line in watchdog_route
                     if (match := re.fullmatch(
                         r"      receiver:\s*([a-z0-9-]+)\s*", line))), None)
    repeat = next((match.group(1) for line in watchdog_route
                   if (match := re.fullmatch(
                       r"      repeat_interval:\s*(\S+)\s*", line))), None)
    if receiver != "external-watchdog" or repeat is None or _duration_seconds(repeat) > 60:
        raise ValueError("alertmanager_watchdog_route")

    receivers = _receiver_blocks(lines)
    required_receivers = {"operator-critical", "operator-warning", "external-watchdog"}
    if not required_receivers.issubset(receivers):
        raise ValueError("alertmanager_receiver_shape")
    receiver_webhooks = {
        name: _receiver_webhooks(receivers[name]) for name in required_receivers
    }
    watchdog_webhooks = receiver_webhooks["external-watchdog"]
    if not any(send_resolved for _, send_resolved in watchdog_webhooks):
        raise ValueError("alertmanager_watchdog_send_resolved")
    all_urls = [url for webhooks in receiver_webhooks.values() for url, _ in webhooks]
    parsed = [urlsplit(url) for url in all_urls]
    if any(value.scheme != "https" or not value.hostname for value in parsed):
        raise ValueError("alertmanager_webhook_url")
    watchdog_hosts = {
        urlsplit(url).hostname for url, _ in watchdog_webhooks
    }
    operator_hosts = {
        urlsplit(url).hostname
        for name in ("operator-critical", "operator-warning")
        for url, _ in receiver_webhooks[name]
    }
    if watchdog_hosts & operator_hosts:
        raise ValueError("alertmanager_watchdog_host_not_independent")


def validate_content(kind: str, content: bytes) -> None:
    if kind == BINARY_32:
        if len(content) != 32:
            raise ValueError("binary_secret_shape")
        return
    text = content.decode("utf-8")
    if PLACEHOLDER.search(text) or "\x00" in text:
        raise ValueError("placeholder_or_binary")
    if kind == SCALAR:
        if not 24 <= len(content) <= 4096 or b"\n" in content or b"\r" in content:
            raise ValueError("scalar_shape")
    elif kind == BOOTSTRAP_ADMIN:
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
    elif kind == REDIS_CONF:
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
    elif kind in {JSON_DOC, API_CONFIG, INGESTION_CONFIG}:
        value = json.loads(text)
        if not isinstance(value, dict):
            raise ValueError("json_shape")
        if kind == JSON_DOC:
            if not 1 <= len(value) <= 2 or any(
                not str(key).isdigit() or not isinstance(item, str) or len(item) < 40
                for key, item in value.items()
            ):
                raise ValueError("json_shape")
        if kind == API_CONFIG:
            if (set(value) != {"ConnectionStrings"}
                    or not isinstance(value["ConnectionStrings"], dict)
                    or set(value["ConnectionStrings"]) != {"Redis"}):
                raise ValueError("api_config_shape")
            redis = value["ConnectionStrings"].get("Redis")
            if not isinstance(redis, str) or "password=" not in redis.lower():
                raise ValueError("api_config_shape")
        if kind == INGESTION_CONFIG:
            if set(value) != {"ExternalApis", "IngestionWorkers"} or not isinstance(value["ExternalApis"], dict):
                raise ValueError("ingestion_config_shape")
            workers = value.get("IngestionWorkers")
            if not isinstance(workers, dict) or not any(
                isinstance(item, dict) and item.get("Enabled") is True for item in workers.values()
            ):
                raise ValueError("ingestion_config_shape")
    elif kind == PRIVATE_PEM:
        if not text.startswith("-----BEGIN PRIVATE KEY-----\n") or "-----BEGIN PUBLIC KEY-----" in text:
            raise ValueError("pem_shape")
    elif kind == PUBLIC_PEM:
        if not text.startswith("-----BEGIN PUBLIC KEY-----\n") or "PRIVATE KEY" in text:
            raise ValueError("pem_shape")
    elif kind == CERTIFICATE_PEM:
        if not text.startswith("-----BEGIN CERTIFICATE-----\n") or "PRIVATE KEY" in text:
            raise ValueError("pem_shape")
    elif kind == ALERTMANAGER:
        validate_alertmanager(text)


def validate_material(root: Path, expected_uid: int,
                      expected_files: dict[str, str]) -> str | None:
    try:
        if not root.is_absolute():
            return "directory_not_absolute"
        unresolved = root
        root_stat = os.lstat(unresolved)
        if stat.S_ISLNK(root_stat.st_mode):
            return "directory_type"
        for parent in unresolved.parents:
            if parent == parent.parent:
                break
            if parent.exists() and stat.S_ISLNK(os.lstat(parent).st_mode):
                return "ancestor_symlink"
        root = unresolved.resolve(strict=True)
        root_stat = os.lstat(root)
        if not stat.S_ISDIR(root_stat.st_mode) or stat.S_ISLNK(root_stat.st_mode):
            return "directory_type"
        if root_stat.st_uid != expected_uid or stat.S_IMODE(root_stat.st_mode) != 0o700:
            return "directory_owner_or_mode"
        names = {item.name for item in root.iterdir()}
        if names != set(expected_files):
            return "file_set"
        for name, kind in expected_files.items():
            path = root / name
            value_stat = os.lstat(path)
            if not stat.S_ISREG(value_stat.st_mode) or stat.S_ISLNK(value_stat.st_mode):
                return "file_type"
            if value_stat.st_nlink != 1 or value_stat.st_uid != expected_uid:
                return "file_owner_or_link"
            if stat.S_IMODE(value_stat.st_mode) not in {0o400, 0o600}:
                return "file_mode"
            validate_content(kind, read_bounded(path))
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError):
        return "content_or_io"
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("purpose", choices=sorted(EXPECTED))
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    expected_uid, expected_files = EXPECTED[args.purpose]
    error = validate_material(args.root, expected_uid, expected_files)
    if error:
        return fail(error)
    print(f"private_material_accepted:{args.purpose}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
