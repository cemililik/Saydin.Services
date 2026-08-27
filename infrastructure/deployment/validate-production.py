#!/usr/bin/env python3
"""Fail-closed validation for a rendered production Compose JSON document."""

from __future__ import annotations

import argparse
import datetime
import ipaddress
import json
import re
import sys
from pathlib import Path
from typing import Any


DIGEST = re.compile(r"^[^\s@:/]+(?:[/:][^\s@:/]+)+@sha256:[0-9a-f]{64}$")
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
    "data_repair_secret": "data-repair",
    "data_repair_input": "data-repair",
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


def managed_login_binding_valid(
        role_prefix: str, purpose: str, login: str, version: str) -> bool:
    if re.fullmatch(r"(?:[1-9]|[12][0-9]|3[0-2])", version) is None:
        return False
    return login == f"{role_prefix}_{purpose}_login_v{version}"


def validate(document: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    services = document.get("services") or {}
    networks = document.get("networks") or {}
    volumes = document.get("volumes") or {}

    # Environment-only scans are bypassed by placeholders hidden in labels,
    # commands, mount paths or extension fields. Scan the complete rendered
    # model before interpreting individual service contracts.
    if PLACEHOLDER.search(json.dumps(document, sort_keys=True)):
        reject(errors, "placeholder_rendered_document")

    required_services = {
        "postgres", "redis", "database-role-bootstrap", "database-migrator",
        "saydin-api", "saydin-price-ingestion", "caddy", "postgres-exporter",
        "redis-exporter", "otel-collector", "prometheus", "alertmanager",
        "tempo", "loki", "blackbox-exporter", "node-exporter", "calendar-release",
        "calendar-activate", "data-quality-audit", "database-backup",
        "database-wal-archive", "data-repair",
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
        "data-repair",
    }

    for name, service in services.items():
        image = str(service.get("image", ""))
        if not DIGEST.fullmatch(image) or image.endswith("0" * 64) or PLACEHOLDER.search(image):
            reject(errors, "image_not_digest_pinned", name)
        if service.get("build") is not None:
            reject(errors, "host_build_forbidden", name)
        if service.get("privileged") not in (None, False):
            reject(errors, "privileged_forbidden", name)
        if service.get("cap_add"):
            reject(errors, "cap_add_forbidden", name)
        if service.get("devices"):
            reject(errors, "devices_forbidden", name)
        if service.get("sysctls"):
            reject(errors, "sysctls_forbidden", name)
        if service.get("group_add"):
            reject(errors, "group_add_forbidden", name)
        if service.get("network_mode"):
            reject(errors, "network_mode_forbidden", name)
        for namespace in ("pid", "ipc", "uts", "userns_mode"):
            if str(service.get(namespace, "")).lower() == "host":
                reject(errors, "host_namespace_forbidden", name)
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
    security_limits = {
        "DistributedSecurityLimiter__RegistrationExactHourlyLimit": "3",
        "DistributedSecurityLimiter__RegistrationExactDailyLimit": "5",
        "DistributedSecurityLimiter__RegistrationNetworkHourlyLimit": "20",
        "DistributedSecurityLimiter__RegistrationNetworkDailyLimit": "100",
        "DistributedSecurityLimiter__RegistrationIpv4ExactHourlyLimit": "60",
        "DistributedSecurityLimiter__RegistrationIpv4NetworkHourlyLimit": "1000",
        "DistributedSecurityLimiter__CalculationNetworkDailyLimit": "500",
    }
    if any(api_env.get(key) != expected for key, expected in security_limits.items()):
        reject(errors, "security_limiter_production_limits_invalid")
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
    if postgres_command_items.count("archive_timeout=300s") != 1:
        reject(errors, "wal_archive_timeout_missing")
    if not all(value in postgres_command for value in (
            "ssl=on", "ssl_cert_file=/run/saydin-secrets/private/server.crt",
            "ssl_key_file=/run/saydin-secrets/private/server.key")):
        reject(errors, "postgres_tls_boundary_missing")
    for service_name in (
            "database-migrator", "saydin-api", "saydin-price-ingestion",
            "calendar-release", "calendar-activate", "data-quality-audit", "data-repair"):
        environment = environment_map(services.get(service_name, {}).get("environment"))
        if environment.get("PGSSLMODE", "").lower() != "require":
            reject(errors, "postgres_client_tls_required", service_name)
    exporter_uri = environment_map(
        services.get("postgres-exporter", {}).get("environment")).get("DATA_SOURCE_URI", "")
    if exporter_uri.count("sslmode=require") != 1 or "sslmode=disable" in exporter_uri.lower():
        reject(errors, "postgres_exporter_tls_required")
    migrator_environment = environment_map(
        services.get("database-migrator", {}).get("environment"))
    role_prefix = migrator_environment.get("SAYDIN_DATABASE_ROLE_PREFIX", "")
    if (not managed_login_binding_valid(
            role_prefix, "migrator", migrator_environment.get("PGUSER", ""),
            migrator_environment.get("SAYDIN_MIGRATOR_LOGIN_VERSION", ""))
            or "SAYDIN_DATABASE_LOGIN_VERSION" in migrator_environment):
        reject(errors, "migrator_login_version_binding_invalid")

    login_bindings = (
        ("saydin-api", "api", "PGUSER", "SAYDIN_DATABASE_LOGIN_VERSION"),
        ("saydin-price-ingestion", "ingestion", "PGUSER", "SAYDIN_DATABASE_LOGIN_VERSION"),
        ("calendar-release", "calendar_importer", "PGUSER", "SAYDIN_DATABASE_LOGIN_VERSION"),
        ("calendar-activate", "calendar_importer", "PGUSER", "SAYDIN_DATABASE_LOGIN_VERSION"),
        ("data-quality-audit", "audit", "PGUSER", "SAYDIN_DATABASE_LOGIN_VERSION"),
        ("data-repair", "ingestion", "PGUSER", "SAYDIN_DATABASE_LOGIN_VERSION"),
        ("postgres-exporter", "exporter", "DATA_SOURCE_USER", "SAYDIN_EXPORTER_LOGIN_VERSION"),
    )
    for service_name, purpose, login_key, version_key in login_bindings:
        binding_environment = environment_map(services.get(service_name, {}).get("environment"))
        if not managed_login_binding_valid(
                role_prefix, purpose, binding_environment.get(login_key, ""),
                binding_environment.get(version_key, "")):
            reject(errors, "managed_login_version_binding_invalid", service_name)

    dqa = services.get("data-quality-audit", {})
    dqa_command = command_text(dqa)
    required_dqa = (
        "--signer-mode oci-kms-instance-principal", "--kms-key-id",
        "--kms-key-version-id", "--kms-crypto-endpoint", "--oci-region",
        "--evidence-public-key /run/saydin-secrets/private/evidence-public.pem",
        "--allowed-evidence-key-ids", "--kms-timeout-seconds 10",
        "--production-target-authority-file /run/saydin-secrets/private/production-target",
    )
    if any(value not in dqa_command for value in required_dqa) \
            or "--evidence-private-key" in dqa_command:
        reject(errors, "dqa_kms_boundary_invalid")

    repair = services.get("data-repair", {})
    repair_env = environment_map(repair.get("environment"))
    repair_audit_env = environment_map(dqa.get("environment"))
    repair_command = repair.get("command")
    expected_repair_environment = {
        "SAYDIN_ENVIRONMENT", "PGHOST", "PGPORT", "PGDATABASE", "PGUSER",
        "PGSSLMODE", "SAYDIN_INGESTION_DATABASE_PASSWORD_FILE",
        "SAYDIN_DEPLOYMENT_ID", "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256",
        "SAYDIN_DATABASE_ROLE_PREFIX", "SAYDIN_DATABASE_LOGIN_VERSION",
        "SAYDIN_DATA_REPAIR_AUDIT_LOGIN", "SAYDIN_DATA_REPAIR_KMS_KEY_ID",
        "SAYDIN_DATA_REPAIR_KMS_KEY_VERSION_ID",
        "SAYDIN_DATA_REPAIR_KMS_CRYPTO_ENDPOINT", "SAYDIN_DATA_REPAIR_OCI_REGION",
    }
    ingestion_env = environment_map(
        services.get("saydin-price-ingestion", {}).get("environment"))
    if (repair.get("profiles") != ["data-repair-operator"]
            or repair.get("user") != "1001:1001"
            or repair_command != ["operator-command-required"]
            or repair.get("restart") != "no"
            or repair.get("depends_on")
            or repair.get("healthcheck")
            or set(repair_env) != expected_repair_environment):
        reject(errors, "data_repair_operator_boundary_invalid")
    if (repair_env.get("SAYDIN_ENVIRONMENT") != "production"
            or repair_env.get("PGHOST") != "postgres"
            or repair_env.get("PGPORT") != "5432"
            or repair_env.get("PGDATABASE") != ingestion_env.get("PGDATABASE")
            or repair_env.get("PGUSER") != ingestion_env.get("PGUSER")
            or repair_env.get("PGSSLMODE", "").lower() != "require"
            or repair_env.get("SAYDIN_INGESTION_DATABASE_PASSWORD_FILE")
            != "/run/saydin-secrets/private/ingestion-current"
            or repair_env.get("SAYDIN_DEPLOYMENT_ID") != ingestion_env.get("SAYDIN_DEPLOYMENT_ID")
            or repair_env.get("SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256")
            != ingestion_env.get("SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256")
            or repair_env.get("SAYDIN_DATABASE_ROLE_PREFIX")
            != ingestion_env.get("SAYDIN_DATABASE_ROLE_PREFIX")
            or repair_env.get("SAYDIN_DATABASE_LOGIN_VERSION")
            != ingestion_env.get("SAYDIN_DATABASE_LOGIN_VERSION")):
        reject(errors, "data_repair_database_boundary_invalid")
    if (repair_env.get("SAYDIN_DATA_REPAIR_AUDIT_LOGIN") != repair_audit_env.get("PGUSER")
            or not repair_env.get("SAYDIN_DATA_REPAIR_KMS_KEY_ID", "").startswith("ocid1.key.")
            or not repair_env.get("SAYDIN_DATA_REPAIR_KMS_KEY_VERSION_ID", "").startswith("ocid1.keyversion.")
            or not repair_env.get("SAYDIN_DATA_REPAIR_KMS_CRYPTO_ENDPOINT", "").startswith("https://")
            or not re.fullmatch(r"[a-z0-9-]{3,63}",
                                repair_env.get("SAYDIN_DATA_REPAIR_OCI_REGION", ""))):
        reject(errors, "data_repair_kms_boundary_invalid")

    bootstrap_command = command_text(services.get("database-role-bootstrap", {}))
    required_bootstrap_passwords = (
        "--migrator-password-file /run/saydin-secrets/private/migrator-current",
        "--api-password-file /run/saydin-secrets/private/api-current",
        "--ingestion-password-file /run/saydin-secrets/private/ingestion-current",
        "--calendar-importer-password-file /run/saydin-secrets/private/calendar_importer-current",
        "--exporter-password-file /run/saydin-secrets/private/exporter-current",
        "--audit-password-file /run/saydin-secrets/private/audit-current",
        "--backup-password-file /run/saydin-secrets/private/backup-v1",
    )
    if ("--backup-v1-valid-until" not in bootstrap_command
            or any(value not in bootstrap_command for value in required_bootstrap_passwords)):
        reject(errors, "backup_bootstrap_contract_missing")
    migrator_env = environment_map(services.get("database-migrator", {}).get("environment"))
    dqa_env = environment_map(dqa.get("environment"))
    backup_env = environment_map(services.get("database-backup", {}).get("environment"))
    valid_until = migrator_env.get("SAYDIN_BACKUP_V1_VALID_UNTIL", "")
    try:
        parsed_valid_until = datetime.datetime.strptime(
            valid_until, "%Y-%m-%dT%H:%M:%SZ"
        ).replace(tzinfo=datetime.timezone.utc)
    except ValueError:
        parsed_valid_until = None
    if parsed_valid_until is None \
            or parsed_valid_until.strftime("%Y-%m-%dT%H:%M:%SZ") != valid_until \
            or dqa_env.get("SAYDIN_BACKUP_V1_VALID_UNTIL") != valid_until \
            or backup_env.get("SAYDIN_BACKUP_V1_VALID_UNTIL") != valid_until:
        reject(errors, "backup_valid_until_contract_invalid")

    for name in (
            "app", "data", "backup-db", "telemetry-ingest", "monitoring-core",
            "monitoring-scrape", "blackbox-control", "host-scrape"):
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
        "probe-egress": "blackbox-exporter",
        "backup-egress": {"database-backup", "database-wal-archive"},
        "kms-egress": "data-quality-audit",
        "data-repair-kms-egress": "data-repair",
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
    if set(api.get("networks") or {}) != {
            "app", "data", "telemetry-ingest", "monitoring-scrape"}:
        reject(errors, "api_network_scope")
    if set((services.get("caddy") or {}).get("networks") or {}) != {"edge", "app"}:
        reject(errors, "proxy_network_scope")
    if set((services.get("prometheus") or {}).get("networks") or {}) != {
            "monitoring-core", "monitoring-scrape", "blackbox-control", "host-scrape"}:
        reject(errors, "prometheus_network_scope")
    if set((services.get("postgres") or {}).get("networks") or {}) != {"data", "backup-db"}:
        reject(errors, "postgres_network_scope")
    if set((services.get("database-role-bootstrap") or {}).get("networks") or {}) != {"backup-db"}:
        reject(errors, "bootstrap_network_scope")
    if set(dqa.get("networks") or {}) != {"data", "kms-egress"}:
        reject(errors, "dqa_network_scope")
    if set(repair.get("networks") or {}) != {"data", "data-repair-kms-egress"}:
        reject(errors, "data_repair_network_scope")
    for backup_service in ("database-backup", "database-wal-archive"):
        service = services.get(backup_service, {})
        if set(service.get("networks") or {}) != {"backup-db", "backup-egress"}:
            reject(errors, "backup_network_scope", backup_service)
        environment = environment_map(service.get("environment"))
        role_prefix = environment.get("SAYDIN_DATABASE_ROLE_PREFIX", "")
        if (environment.get("PGHOST") != "postgres-backup"
                or environment.get("PGSSLMODE", "").lower() != "require"
                or environment.get("PGCONNECT_TIMEOUT") != "10"
                or not re.fullmatch(r"saydin_[a-z][a-z0-9_]{2}_[0-9a-f]{24}", role_prefix)
                or environment.get("PGUSER") != f"{role_prefix}_backup_login_v1"
                or not re.fullmatch(r"[0-9a-f]{64}", environment.get(
                    "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256", ""))):
            reject(errors, "backup_connection_boundary_invalid", backup_service)

    wal_environment = environment_map(
        services.get("database-wal-archive", {}).get("environment"))
    if wal_environment.get("SAYDIN_BACKUP_WAL_UPLOAD_INTERVAL_SECONDS") != "300":
        reject(errors, "backup_wal_upload_interval_invalid")
    wal_minimum_free = wal_environment.get("SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES", "")
    if (not wal_minimum_free.isdigit()
            or int(wal_minimum_free) < 96 * 1024 * 1024 * 1024):
        reject(errors, "backup_wal_spool_capacity_invalid")

    base_backup = services.get("database-backup", {})
    base_environment = environment_map(base_backup.get("environment"))
    minimum_free = base_environment.get("SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES", "")
    if (base_environment.get("SAYDIN_BACKUP_BASE_STAGING_DIR")
            != "/var/lib/saydin-backup/base-staging"
            or not minimum_free.isdigit() or int(minimum_free) < 8 * 1024 * 1024 * 1024):
        reject(errors, "backup_base_staging_environment_invalid")
    base_mounts = {
        (str(item.get("source", "")), str(item.get("target", "")), bool(item.get("read_only")))
        for item in base_backup.get("volumes", []) if isinstance(item, dict)
    }
    expected_base_mount = (
        "backup_base_staging", "/var/lib/saydin-backup/base-staging", False)
    if expected_base_mount not in base_mounts or any(
            str(item.get("source", "")) == "backup_base_staging"
            for service_name, service in services.items() if service_name != "database-backup"
            for item in service.get("volumes", []) if isinstance(item, dict)):
        reject(errors, "backup_base_staging_mount_invalid")
    if not (volumes.get("backup_base_staging") or {}).get("external"):
        reject(errors, "backup_base_staging_volume_not_external")
    base_tmpfs = [str(item) for item in base_backup.get("tmpfs", [])]
    if base_tmpfs != ["/tmp:uid=1001,gid=1001,mode=0700,size=64m"]:
        reject(errors, "backup_tmpfs_boundary_invalid")
    for name in ("postgres", "redis"):
        if "edge" in (services.get(name, {}).get("networks") or {}):
            reject(errors, "data_service_on_edge", name)

    expected_monitoring_networks = {
        "saydin-price-ingestion": {"data", "telemetry-ingest", "provider-egress"},
        "postgres-exporter": {"data", "monitoring-scrape"},
        "redis-exporter": {"data", "monitoring-scrape"},
        "otel-collector": {"telemetry-ingest", "monitoring-core"},
        "tempo": {"monitoring-core"},
        "loki": {"monitoring-core"},
        "alertmanager": {"monitoring-core", "alert-egress"},
        "blackbox-exporter": {"blackbox-control", "probe-egress"},
        "node-exporter": {"host-scrape"},
    }
    for service_name, expected_networks in expected_monitoring_networks.items():
        if set((services.get(service_name) or {}).get("networks") or {}) != expected_networks:
            reject(errors, "monitoring_network_scope", service_name)

    for name, volume in volumes.items():
        if name in PRIVATE_VOLUMES and not (volume or {}).get("external"):
            reject(errors, "private_volume_not_external", name)

    for volume_name in ("otel_queue", "tempo_data", "loki_data"):
        if not (volumes.get(volume_name) or {}).get("external"):
            reject(errors, "telemetry_volume_not_external", volume_name)

    repair_mounts = {
        (str(item.get("source", "")), str(item.get("target", "")), bool(item.get("read_only")))
        for item in repair.get("volumes", []) if isinstance(item, dict)
    }
    expected_repair_mounts = {
        ("data_repair_secret", "/run/saydin-secrets", True),
        ("data_repair_input", "/run/repair", True),
        ("data_repair_receipts", "/var/lib/saydin/repair-receipts", False),
    }
    receipt_consumers = {
        service_name for service_name, service in services.items()
        for item in service.get("volumes", []) if isinstance(item, dict)
        if item.get("source") == "data_repair_receipts"
    }
    if (repair_mounts != expected_repair_mounts
            or receipt_consumers != {"data-repair"}
            or not (volumes.get("data_repair_receipts") or {}).get("external")
            or [str(item) for item in repair.get("tmpfs", [])]
            != ["/tmp:uid=1001,gid=1001,mode=0700,size=32m"]):
        reject(errors, "data_repair_volume_boundary_invalid")

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
        actual = {
            (str(item.get("source", "")), str(item.get("target", "")), bool(item.get("read_only")))
            for item in service.get("volumes", []) if isinstance(item, dict)
        }
        if (expected[0], expected[1], False) not in actual:
            reject(errors, "telemetry_durable_volume_missing", service_name)

    expected_bind_targets = {
        "saydin-api": {"/app/geoip"},
        "caddy": {"/etc/caddy/Caddyfile"},
        "otel-collector": {"/etc/otelcol/config.yml"},
        "tempo": {"/etc/tempo/config.yml"},
        "loki": {"/etc/loki/config.yml"},
        "prometheus": {"/etc/prometheus/prometheus.yml", "/etc/prometheus/rules"},
        "blackbox-exporter": {"/etc/blackbox/config.yml"},
        "node-exporter": {"/host"},
    }
    for service_name, service in services.items():
        binds = [item for item in service.get("volumes", [])
                 if isinstance(item, dict) and item.get("type") == "bind"]
        actual_targets = {str(item.get("target", "")) for item in binds}
        if actual_targets != expected_bind_targets.get(service_name, set()):
            reject(errors, "bind_mount_target_set_invalid", service_name)
        for mount in binds:
            source = str(mount.get("source", ""))
            target = str(mount.get("target", ""))
            if mount.get("read_only") is not True:
                reject(errors, "bind_mount_not_read_only", service_name)
            if source.endswith("/docker.sock") or target.endswith("/docker.sock"):
                reject(errors, "docker_socket_mount_forbidden", service_name)
            if source == "/":
                propagation = (mount.get("bind") or {}).get("propagation")
                if service_name != "node-exporter" or target != "/host" or propagation != "rslave":
                    reject(errors, "host_root_mount_forbidden", service_name)
    node_command = command_text(services.get("node-exporter", {}))
    for token in (
            "--path.rootfs=/host", "--collector.disable-defaults", "--collector.cpu",
            "--collector.filesystem", "--collector.loadavg", "--collector.meminfo",
            "--collector.stat", "--collector.textfile", "--collector.time", "--collector.uname"):
        if token not in node_command:
            reject(errors, "node_exporter_collector_allowlist_invalid")

    health_contracts = {
        "caddy": ("health/live",),
        "prometheus": ("promtool", "check", "healthy"),
        "alertmanager": ("/-/ready",),
        "tempo": ("/ready",),
        "loki": ("/usr/bin/loki", "-version"),
    }
    for service_name, required_tokens in health_contracts.items():
        health_text = " ".join(str(item) for item in
                               ((services.get(service_name, {}).get("healthcheck") or {}).get("test") or []))
        if any(token not in health_text for token in required_tokens):
            reject(errors, "monitoring_healthcheck_invalid", service_name)

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
