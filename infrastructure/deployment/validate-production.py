#!/usr/bin/env python3
"""Fail-closed validation for a rendered production Compose JSON document."""

from __future__ import annotations

import argparse
import ipaddress
import json
import re
import sys
from pathlib import Path
from typing import Any


DIGEST = re.compile(r"^[^\s@:]+(?:[/:][^\s@]+)+@sha256:[0-9a-f]{64}$")
HEX40 = re.compile(r"^[0-9a-f]{40}$")
PLACEHOLDER = re.compile(r"change[_-]?me|example\.invalid|placeholder|replace[_-]?me", re.I)
SECRET_KEY = re.compile(r"password|secret|token|api[_-]?key|app[_-]?id|connectionstrings", re.I)
SAFE_SECRET_REFERENCE = re.compile(r"(?:_file|file$|key_id$)", re.I)
FORBIDDEN_COMMAND = re.compile(
    r"password\s*=|--requirepass\b|apikey\s*=|app_id\s*=|authorization\s*[:=]|bearer\s+",
    re.I,
)
PRIVATE_VOLUMES = {
    "postgres_secret": "postgres",
    "redis_secret": "redis",
    "bootstrap_secret": "database-role-bootstrap",
    "migrator_secret": "database-migrator",
    "api_secret": "saydin-api",
    "api_config": "saydin-api",
    "ingestion_secret": "saydin-price-ingestion",
    "ingestion_config": "saydin-price-ingestion",
    "calendar_secret": {"calendar-release", "calendar-activate"},
    "exporter_secret": "postgres-exporter",
    "redis_exporter_secret": "redis-exporter",
    "alertmanager_secret": "alertmanager",
    "audit_secret": "data-quality-audit",
    "audit_input": "data-quality-audit",
    "backup_secret": {"database-backup", "database-wal-archive"},
}


def reject(errors: list[str], code: str, service: str | None = None) -> None:
    errors.append(f"{code}:{service}" if service else code)


def environment_map(value: Any) -> dict[str, str]:
    if isinstance(value, dict):
        return {str(key): "" if item is None else str(item) for key, item in value.items()}
    result: dict[str, str] = {}
    for item in value or []:
        key, _, raw = str(item).partition("=")
        result[key] = raw
    return result


def command_text(service: dict[str, Any]) -> str:
    parts: list[str] = []
    for key in ("entrypoint", "command"):
        value = service.get(key, [])
        parts.extend(value if isinstance(value, list) else [str(value)])
    return " ".join(str(part) for part in parts)


def is_exact_dns_hostname(value: str) -> bool:
    """Accept one canonical DNS hostname, never a URL, wildcard, IP, or host list."""
    if not 1 <= len(value) <= 253 or value != value.lower() or value.endswith("."):
        return False
    if any(token in value for token in ("*", "://", "/", ";", ",", " ")):
        return False
    try:
        ipaddress.ip_address(value)
        return False
    except ValueError:
        pass
    labels = value.split(".")
    return len(labels) >= 2 and all(
        1 <= len(label) <= 63
        and re.fullmatch(r"[a-z0-9](?:[a-z0-9-]*[a-z0-9])?", label) is not None
        for label in labels
    )


def exact_private_proxy_network(value: str) -> ipaddress.IPv4Network | ipaddress.IPv6Network | None:
    """Parse one bounded private proxy CIDR in canonical network notation."""
    if not value or any(separator in value for separator in (",", ";", " ")):
        return None
    try:
        network = ipaddress.ip_network(value, strict=True)
    except ValueError:
        return None
    minimum_prefix = 24 if network.version == 4 else 64
    if (network.prefixlen < minimum_prefix or not network.is_private
            or network.is_loopback or network.is_multicast or network.is_unspecified):
        return None
    return network


def validate(document: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    services = document.get("services") or {}
    networks = document.get("networks") or {}
    volumes = document.get("volumes") or {}

    required_services = {
        "postgres", "redis", "database-role-bootstrap", "database-migrator",
        "saydin-api", "saydin-price-ingestion", "caddy", "postgres-exporter",
        "redis-exporter", "otel-collector", "prometheus", "alertmanager",
        "tempo", "loki", "blackbox-exporter", "node-exporter", "calendar-release",
        "calendar-activate", "data-quality-audit", "database-backup",
        "database-wal-archive",
    }
    missing = sorted(required_services - services.keys())
    if missing:
        reject(errors, "service_set_missing")

    forbidden_services = {"pgadmin", "redis-insight", "aspire-dashboard", "tests"}
    if forbidden_services.intersection(services):
        reject(errors, "admin_or_dev_service_present")

    first_party = {
        "database-role-bootstrap", "database-migrator", "saydin-api",
        "saydin-price-ingestion", "caddy", "calendar-release", "calendar-activate",
        "data-quality-audit", "database-backup", "database-wal-archive",
    }

    for name, service in services.items():
        image = str(service.get("image", ""))
        if not DIGEST.fullmatch(image) or image.endswith("0" * 64) or PLACEHOLDER.search(image):
            reject(errors, "image_not_digest_pinned", name)
        if service.get("build") is not None:
            reject(errors, "host_build_forbidden", name)
        if name in first_party:
            labels = service.get("labels") or {}
            if not str(labels.get("io.saydin.image-class", "")).startswith("first-party"):
                reject(errors, "first_party_label_missing", name)
            if not HEX40.fullmatch(str(labels.get("org.opencontainers.image.revision", ""))):
                reject(errors, "release_revision_label_invalid", name)
            if not labels.get("io.saydin.deployment-id"):
                reject(errors, "deployment_label_missing", name)

        if service.get("read_only") is not True:
            reject(errors, "read_only_missing", name)
        caps = {str(item).upper() for item in service.get("cap_drop", [])}
        if "ALL" not in caps:
            reject(errors, "cap_drop_all_missing", name)
        security = {str(item).lower() for item in service.get("security_opt", [])}
        if "no-new-privileges:true" not in security:
            reject(errors, "no_new_privileges_missing", name)
        user = str(service.get("user", ""))
        if not user or user.split(":", 1)[0] in {"0", "root"}:
            reject(errors, "nonroot_user_missing", name)
        if int(service.get("pids_limit", 0) or 0) <= 0:
            reject(errors, "pids_limit_missing", name)
        if not service.get("cpus") or not service.get("mem_limit"):
            reject(errors, "resource_limit_missing", name)
        if not service.get("stop_grace_period"):
            reject(errors, "stop_grace_missing", name)
        logging = service.get("logging") or {}
        options = logging.get("options") or {}
        if logging.get("driver") != "json-file" or not options.get("max-size") or not options.get("max-file"):
            reject(errors, "bounded_logging_missing", name)

        for key, value in environment_map(service.get("environment")).items():
            if PLACEHOLDER.search(value):
                reject(errors, "placeholder_environment", name)
            if SECRET_KEY.search(key):
                if not SAFE_SECRET_REFERENCE.search(key) or not value.startswith("/"):
                    reject(errors, "raw_secret_environment", name)
            if SECRET_KEY.search(value) and not value.startswith("/"):
                reject(errors, "secret_shaped_environment_value", name)
        if FORBIDDEN_COMMAND.search(command_text(service)):
            reject(errors, "secret_in_argv", name)

        ports = service.get("ports") or []
        if name != "caddy" and ports:
            reject(errors, "internal_port_published", name)
        if name == "caddy":
            published = sorted(str(port.get("published")) for port in ports if isinstance(port, dict))
            if published != ["443", "80"]:
                reject(errors, "caddy_public_ports_invalid", name)

    api = services.get("saydin-api", {})
    api_env = environment_map(api.get("environment"))
    if api_env.get("ASPNETCORE_ENVIRONMENT") != "Production":
        reject(errors, "api_not_production")
    if api_env.get("ApiRuntime__PublicPort") != "8080" \
            or api_env.get("ApiRuntime__ManagementPort") != "9090":
        reject(errors, "api_port_boundary_invalid")
    allowed_hosts = api_env.get("AllowedHosts", "").split(";")
    caddy_public_host = environment_map(
        services.get("caddy", {}).get("environment")).get("SAYDIN_PUBLIC_HOST", "")
    if (len(allowed_hosts) != 2 or allowed_hosts[1] != "saydin-api"
            or not is_exact_dns_hostname(allowed_hosts[0])
            or allowed_hosts[0] != caddy_public_host):
        reject(errors, "allowed_hosts_invalid")
    if api_env.get("DistributedSecurityLimiter__Enabled", "").lower() != "true":
        reject(errors, "security_limiter_disabled")
    proxy_network = exact_private_proxy_network(api_env.get("ForwardedHeaders__KnownNetworks", ""))
    if proxy_network is None:
        reject(errors, "trusted_proxy_invalid")
    if api_env.get("ForwardedHeaders__ForwardLimit") != "1":
        reject(errors, "trusted_proxy_forward_limit_invalid")
    if not HEX40.fullmatch(api_env.get("SAYDIN_GIT_SHA", "")):
        reject(errors, "git_sha_invalid")
    service_version = api_env.get("SAYDIN_SERVICE_VERSION", "")
    release_version = api_env.get("SAYDIN_RELEASE_VERSION", "")
    if (not 1 <= len(service_version) <= 128
            or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+:-]*", service_version)
            or service_version.lower() in {
                "0.0.0", "1.0.0", "dev", "development", "latest", "local",
                "snapshot", "todo", "unknown", "unset",
            }):
        reject(errors, "service_version_invalid")
    if service_version != release_version:
        reject(errors, "service_version_release_mismatch")
    api_health = " ".join(str(value) for value in (api.get("healthcheck") or {}).get("test") or [])
    if ("http://127.0.0.1:8080/health/live" not in api_health
            or "Host: saydin-api" not in api_health):
        reject(errors, "api_liveness_probe_invalid")

    ingestion_env = environment_map(services.get("saydin-price-ingestion", {}).get("environment"))
    if ingestion_env.get("DOTNET_ENVIRONMENT") != "Production":
        reject(errors, "ingestion_not_production")

    postgres_service = services.get("postgres", {})
    postgres_command_value = postgres_service.get("command", [])
    postgres_command_items = [str(item) for item in (
        postgres_command_value if isinstance(postgres_command_value, list)
        else [postgres_command_value]
    )]
    postgres_command = command_text(postgres_service)
    if postgres_command_items.count("max_slot_wal_keep_size=8GB") != 1:
        reject(errors, "replication_slot_wal_bound_missing")
    if postgres_command_items.count("wal_keep_size=8GB") != 1:
        reject(errors, "basebackup_fetch_wal_window_missing")
    if not all(value in postgres_command for value in (
            "ssl=on", "ssl_cert_file=/run/saydin-secrets/private/server.crt",
            "ssl_key_file=/run/saydin-secrets/private/server.key")):
        reject(errors, "postgres_tls_boundary_missing")

    dqa = services.get("data-quality-audit", {})
    dqa_command = command_text(dqa)
    required_dqa = (
        "--signer-mode oci-kms-instance-principal", "--kms-key-id",
        "--kms-key-version-id", "--kms-crypto-endpoint", "--oci-region",
        "--evidence-public-key /run/saydin-secrets/private/evidence-public.pem",
        "--allowed-evidence-key-ids", "--kms-timeout-seconds 10",
    )
    if any(value not in dqa_command for value in required_dqa) \
            or "--evidence-private-key" in dqa_command:
        reject(errors, "dqa_kms_boundary_invalid")

    bootstrap_command = command_text(services.get("database-role-bootstrap", {}))
    if "--backup-v1-valid-until" not in bootstrap_command \
            or "--backup-password-file /run/saydin-secrets/private/backup-v1" not in bootstrap_command:
        reject(errors, "backup_bootstrap_contract_missing")
    migrator_env = environment_map(services.get("database-migrator", {}).get("environment"))
    dqa_env = environment_map(dqa.get("environment"))
    valid_until = migrator_env.get("SAYDIN_BACKUP_V1_VALID_UNTIL", "")
    if not re.fullmatch(r"20[0-9]{2}-[01][0-9]-[0-3][0-9]T[0-2][0-9]:[0-5][0-9]:[0-5][0-9]Z", valid_until) \
            or dqa_env.get("SAYDIN_BACKUP_V1_VALID_UNTIL") != valid_until:
        reject(errors, "backup_valid_until_contract_invalid")

    for name in ("app", "data", "backup-db", "management"):
        if not (networks.get(name) or {}).get("internal"):
            reject(errors, "private_network_not_internal", name)
    if (networks.get("edge") or {}).get("internal"):
        reject(errors, "edge_network_internal")
    app_ipam = (networks.get("app") or {}).get("ipam") or {}
    app_subnets = [
        str(item.get("subnet", ""))
        for item in app_ipam.get("config") or []
        if isinstance(item, dict)
    ]
    if proxy_network is None or app_subnets != [str(proxy_network)]:
        reject(errors, "trusted_proxy_network_binding_invalid")
    backup_ipam = (networks.get("backup-db") or {}).get("ipam") or {}
    backup_subnets = [
        str(item.get("subnet", "")) for item in backup_ipam.get("config") or []
        if isinstance(item, dict)
    ]
    try:
        backup_network = ipaddress.ip_network(backup_subnets[0], strict=True) \
            if len(backup_subnets) == 1 else None
    except ValueError:
        backup_network = None
    if (backup_network is None or backup_network.version != 4 or not backup_network.is_private
            or backup_network.prefixlen != 28
            or proxy_network is not None and backup_network.overlaps(proxy_network)):
        reject(errors, "backup_database_network_invalid")
    expected_egress = {
        "provider-egress": "saydin-price-ingestion",
        "alert-egress": "alertmanager",
        "backup-egress": {"database-backup", "database-wal-archive"},
        "kms-egress": "data-quality-audit",
    }
    for network_name, consumer in expected_egress.items():
        if (networks.get(network_name) or {}).get("internal"):
            reject(errors, "egress_network_internal", network_name)
        attached = {
            service_name for service_name, service in services.items()
            if network_name in (service.get("networks") or {})
        }
        expected_consumers = consumer if isinstance(consumer, set) else {consumer}
        if attached != expected_consumers:
            reject(errors, "egress_network_scope", network_name)

    if "data" in (services.get("caddy", {}).get("networks") or {}):
        reject(errors, "proxy_on_data_network")
    if set(api.get("networks") or {}) != {"app", "data", "management"}:
        reject(errors, "api_network_scope")
    if set((services.get("caddy") or {}).get("networks") or {}) != {"edge", "app"}:
        reject(errors, "proxy_network_scope")
    if set((services.get("prometheus") or {}).get("networks") or {}) != {"management"}:
        reject(errors, "prometheus_network_scope")
    if set((services.get("postgres") or {}).get("networks") or {}) != {"data", "backup-db"}:
        reject(errors, "postgres_network_scope")
    if set((services.get("database-role-bootstrap") or {}).get("networks") or {}) != {"backup-db"}:
        reject(errors, "bootstrap_network_scope")
    if set(dqa.get("networks") or {}) != {"data", "kms-egress"}:
        reject(errors, "dqa_network_scope")
    for backup_service in ("database-backup", "database-wal-archive"):
        service = services.get(backup_service, {})
        if set(service.get("networks") or {}) != {"backup-db", "backup-egress"}:
            reject(errors, "backup_network_scope", backup_service)
        environment = environment_map(service.get("environment"))
        role_prefix = environment.get("SAYDIN_DATABASE_ROLE_PREFIX", "")
        if (environment.get("PGHOST") != "postgres-backup"
                or environment.get("PGSSLMODE", "").lower() != "require"
                or not re.fullmatch(r"saydin_[a-z][a-z0-9_]{2}_[0-9a-f]{24}", role_prefix)
                or environment.get("PGUSER") != f"{role_prefix}_backup_login_v1"
                or not re.fullmatch(r"[0-9a-f]{64}", environment.get(
                    "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256", ""))):
            reject(errors, "backup_connection_boundary_invalid", backup_service)
    for name in ("postgres", "redis"):
        if "edge" in (services.get(name, {}).get("networks") or {}):
            reject(errors, "data_service_on_edge", name)

    for name, volume in volumes.items():
        if name in PRIVATE_VOLUMES and not (volume or {}).get("external"):
            reject(errors, "private_volume_not_external", name)

    for volume_name in ("otel_queue", "tempo_data", "loki_data"):
        if not (volumes.get(volume_name) or {}).get("external"):
            reject(errors, "telemetry_volume_not_external", volume_name)

    for service_name, service in services.items():
        for mount in service.get("volumes", []):
            if not isinstance(mount, dict):
                continue
            source = str(mount.get("source", ""))
            expected_consumer = PRIVATE_VOLUMES.get(source)
            if expected_consumer is None:
                continue
            consumers = expected_consumer if isinstance(expected_consumer, set) else {expected_consumer}
            if service_name not in consumers:
                reject(errors, "private_volume_wrong_consumer", service_name)
            if mount.get("read_only") is not True:
                reject(errors, "private_volume_not_read_only", service_name)

    mounts = {
        (str(item.get("source", "")), str(item.get("target", "")), bool(item.get("read_only")))
        for item in api.get("volumes", []) if isinstance(item, dict)
    }
    if not any(target == "/app/appsettings.Production.json" and read_only for _, target, read_only in mounts):
        reject(errors, "api_private_config_missing")

    telemetry_mounts = {
        "otel-collector": ("otel_queue", "/var/lib/otelcol/queue"),
        "tempo": ("tempo_data", "/var/tempo"),
        "loki": ("loki_data", "/var/loki"),
    }
    for service_name, expected in telemetry_mounts.items():
        service = services.get(service_name, {})
        if set(service.get("networks") or {}) != {"management"}:
            reject(errors, "telemetry_network_scope", service_name)
        actual = {
            (str(item.get("source", "")), str(item.get("target", "")), bool(item.get("read_only")))
            for item in service.get("volumes", []) if isinstance(item, dict)
        }
        if (expected[0], expected[1], False) not in actual:
            reject(errors, "telemetry_durable_volume_missing", service_name)

    return sorted(set(errors))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("compose_json", type=Path)
    args = parser.parse_args()
    try:
        document = json.loads(args.compose_json.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        print("production_validation_failed:compose_json_invalid", file=sys.stderr)
        return 2
    errors = validate(document)
    if errors:
        for error in errors:
            print(f"production_validation_failed:{error}", file=sys.stderr)
        return 2
    print("production_validation_passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
