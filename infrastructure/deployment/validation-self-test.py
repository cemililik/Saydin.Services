#!/usr/bin/env python3
"""Mutation tests for the production manifest validator."""

from __future__ import annotations

import copy
import importlib.util
import json
import sys
from pathlib import Path


# Deliberately over-broad RFC1918 range. It exists only as a mutation the production
# validator must reject; nothing in this self-test binds or dials it.
OVER_BROAD_PRIVATE_CIDR = "172.16.0.0/12"  # NOSONAR


def load_validator(directory: Path):
    spec = importlib.util.spec_from_file_location(
        "saydin_production_validator", directory / "validate-production.py")
    if spec is None or spec.loader is None:
        raise RuntimeError("validator_load_failed")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    if len(sys.argv) != 2:
        print("production_validation_self_test_failed:compose_json_required", file=sys.stderr)
        return 64
    source = Path(sys.argv[1])
    validator = load_validator(Path(__file__).resolve().parent)
    baseline = json.loads(source.read_text(encoding="utf-8"))
    if validator.validate(baseline):
        print("production_validation_self_test_failed:baseline", file=sys.stderr)
        return 2

    mutations = {
        "development": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("ASPNETCORE_ENVIRONMENT", "Development"),
        "wildcard_host": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("AllowedHosts", "*"),
        "management_host_missing": lambda value: value["services"]["saydin-api"]["environment"].__setitem__(
            "AllowedHosts", value["services"]["caddy"]["environment"]["SAYDIN_PUBLIC_HOST"]),
        "limiter_disabled": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("DistributedSecurityLimiter__Enabled", "false"),
        "registration_limit_missing": lambda value: value["services"]["saydin-api"]["environment"].pop(
            "DistributedSecurityLimiter__RegistrationExactHourlyLimit"),
        "network_limit_unbounded": lambda value: value["services"]["saydin-api"]["environment"].__setitem__(
            "DistributedSecurityLimiter__CalculationNetworkDailyLimit", "500000"),
        "raw_secret": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("REDIS_PASSWORD", "sentinel-secret"),
        "mutable_image": lambda value: value["services"]["saydin-api"].__setitem__("image", "ghcr.io/example/saydin-api:latest"),
        "internal_port": lambda value: value["services"]["postgres"].__setitem__("ports", [{"target": 5432, "published": "5432"}]),
        "read_write_root": lambda value: value["services"]["saydin-api"].__setitem__("read_only", False),
        "missing_cap_drop": lambda value: value["services"]["saydin-api"].__setitem__("cap_drop", []),
        "privileged": lambda value: value["services"]["redis"].__setitem__("privileged", True),
        "cap_add": lambda value: value["services"]["redis"].__setitem__("cap_add", ["SYS_PTRACE"]),
        "host_pid": lambda value: value["services"]["redis"].__setitem__("pid", "host"),
        "host_network": lambda value: value["services"]["redis"].__setitem__("network_mode", "host"),
        "device_escape": lambda value: value["services"]["redis"].__setitem__("devices", ["/dev/kmsg:/dev/kmsg"]),
        "kernel_sysctl": lambda value: value["services"]["redis"].__setitem__("sysctls", {"net.ipv4.ip_forward": "1"}),
        "host_group": lambda value: value["services"]["redis"].__setitem__("group_add", ["docker"]),
        "docker_socket": lambda value: value["services"]["redis"].setdefault("volumes", []).append({
            "type": "bind", "source": "/var/run/docker.sock", "target": "/var/run/docker.sock",
            "read_only": True,
        }),
        "host_root_wrong_consumer": lambda value: value["services"]["redis"].setdefault("volumes", []).append({
            "type": "bind", "source": "/", "target": "/host", "read_only": True,
            "bind": {"propagation": "rslave"},
        }),
        "placeholder_label": lambda value: value["services"]["saydin-api"]["labels"].__setitem__(
            "io.saydin.operator-note", "CHANGE_ME"),
        "placeholder": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("SAYDIN_DEPLOYMENT_ID", "CHANGE_ME"),
        "unbounded_replication_slot": lambda value: value["services"]["postgres"].__setitem__("command", ["postgres"]),
        "backup_fetch_wal_window_missing": lambda value: value["services"]["postgres"].__setitem__(
            "command", [item for item in value["services"]["postgres"]["command"]
                        if item != "wal_keep_size=8GB"]),
        "backup_archive_timeout_missing": lambda value: value["services"]["postgres"].__setitem__(
            "command", [item for item in value["services"]["postgres"]["command"]
                        if item != "archive_timeout=300s"]),
        "missing_trace_backend": lambda value: value["services"].pop("tempo"),
        "telemetry_public_port": lambda value: value["services"]["loki"].__setitem__("ports", [{"target": 3100, "published": "3100"}]),
        "telemetry_ephemeral": lambda value: value["services"]["otel-collector"].__setitem__("volumes", []),
        "collapsed_api_ports": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("ApiRuntime__ManagementPort", "8080"),
        "readiness_as_liveness": lambda value: value["services"]["saydin-api"]["healthcheck"].__setitem__("test", ["CMD", "curl", "-fsS", "-H", "Host: saydin-api", "http://127.0.0.1:8080/health/ready"]),
        "liveness_host_missing": lambda value: value["services"]["saydin-api"]["healthcheck"].__setitem__("test", ["CMD", "curl", "-fsS", "http://127.0.0.1:8080/health/live"]),
        "service_version_missing": lambda value: value["services"]["saydin-api"]["environment"].pop("SAYDIN_SERVICE_VERSION"),
        "service_version_unbound": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("SAYDIN_SERVICE_VERSION", "different-release"),
        "malformed_host": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("AllowedHosts", "https://api.validation.test"),
        "broad_proxy_cidr": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("ForwardedHeaders__KnownNetworks", OVER_BROAD_PRIVATE_CIDR),
        "proxy_forward_limit": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("ForwardedHeaders__ForwardLimit", "2"),
        "api_network_scope": lambda value: value["services"]["saydin-api"].__setitem__("networks", ["app", "data"]),
        "api_on_monitoring_core": lambda value: value["services"]["saydin-api"].__setitem__(
            "networks", ["app", "data", "telemetry-ingest", "monitoring-core"]),
        "blackbox_on_scrape": lambda value: value["services"]["blackbox-exporter"].__setitem__(
            "networks", ["monitoring-scrape", "probe-egress"]),
        "node_broad_collectors": lambda value: value["services"]["node-exporter"].__setitem__(
            "command", ["--path.rootfs=/host"]),
        "backup_plaintext": lambda value: value["services"]["database-backup"]["environment"].__setitem__("PGSSLMODE", "Disable"),
        "application_database_plaintext": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("PGSSLMODE", "Disable"),
        "exporter_database_plaintext": lambda value: value["services"]["postgres-exporter"]["environment"].__setitem__("DATA_SOURCE_URI", "postgres:5432/saydin?sslmode=disable"),
        "migrator_login_version_misbound": lambda value: value["services"]["database-migrator"]["environment"].__setitem__("SAYDIN_DATABASE_LOGIN_VERSION", value["services"]["database-migrator"]["environment"].pop("SAYDIN_MIGRATOR_LOGIN_VERSION")),
        "backup_broad_network": lambda value: value["networks"]["backup-db"]["ipam"]["config"][0].__setitem__("subnet", OVER_BROAD_PRIVATE_CIDR),
        "backup_on_app_data": lambda value: value["services"]["database-backup"].__setitem__("networks", ["data", "backup-egress"]),
        "backup_staging_tmpfs": lambda value: value["services"]["database-backup"].__setitem__(
            "volumes", [item for item in value["services"]["database-backup"]["volumes"]
                        if item.get("source") != "backup_base_staging"]),
        "backup_staging_read_only": lambda value: next(
            item for item in value["services"]["database-backup"]["volumes"]
            if item.get("source") == "backup_base_staging").__setitem__("read_only", True),
        "backup_staging_capacity_small": lambda value: value["services"]["database-backup"]["environment"].__setitem__(
            "SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES", "1073741824"),
        "backup_tmpfs_unbounded": lambda value: value["services"]["database-backup"].__setitem__(
            "tmpfs", ["/tmp:uid=1001,gid=1001,mode=0700,size=2g"]),
        "backup_wal_upload_interval_slow": lambda value: value["services"]["database-wal-archive"]["environment"].__setitem__(
            "SAYDIN_BACKUP_WAL_UPLOAD_INTERVAL_SECONDS", "900"),
        "backup_wal_spool_capacity_small": lambda value: value["services"]["database-wal-archive"]["environment"].__setitem__(
            "SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES", "68719476736"),
        "backup_validity_malformed_calendar": lambda value: value["services"]["database-migrator"]["environment"].__setitem__(
            "SAYDIN_BACKUP_V1_VALID_UNTIL", "2026-02-31T00:00:00Z"),
        "backup_validity_binding_mismatch": lambda value: value["services"]["database-backup"]["environment"].__setitem__(
            "SAYDIN_BACKUP_V1_VALID_UNTIL", "2026-10-20T00:00:00Z"),
        "dqa_private_key": lambda value: value["services"]["data-quality-audit"]["command"].extend(["--evidence-private-key", "/run/private/key.pem"]),
        "dqa_no_kms_egress": lambda value: value["services"]["data-quality-audit"].__setitem__("networks", ["data"]),
        "missing_data_repair": lambda value: value["services"].pop("data-repair"),
        "data_repair_profile_missing": lambda value: value["services"]["data-repair"].__setitem__("profiles", []),
        "data_repair_user_drift": lambda value: value["services"]["data-repair"].__setitem__(
            "user", "1002:1002"),
        "data_repair_default_apply": lambda value: value["services"]["data-repair"].__setitem__("command", ["apply"]),
        "data_repair_mutable_image": lambda value: value["services"]["data-repair"].__setitem__(
            "image", "ghcr.io/example/saydin-data-repair:latest"),
        "data_repair_dependency": lambda value: value["services"]["data-repair"].__setitem__(
            "depends_on", {"database-migrator": {"condition": "service_completed_successfully"}}),
        "data_repair_no_kms_egress": lambda value: value["services"]["data-repair"].__setitem__(
            "networks", ["data"]),
        "data_repair_input_writable": lambda value: next(
            item for item in value["services"]["data-repair"]["volumes"]
            if item.get("source") == "data_repair_input").__setitem__("read_only", False),
        "data_repair_receipts_read_only": lambda value: next(
            item for item in value["services"]["data-repair"]["volumes"]
            if item.get("source") == "data_repair_receipts").__setitem__("read_only", True),
        "data_repair_receipts_shared": lambda value: value["services"]["redis"].setdefault(
            "volumes", []).append({"type": "volume", "source": "data_repair_receipts",
                                   "target": "/repair-receipts", "read_only": True}),
        "data_repair_receipts_ephemeral": lambda value: value["volumes"][
            "data_repair_receipts"].__setitem__("external", False),
        "data_repair_kms_missing": lambda value: value["services"]["data-repair"][
            "environment"].pop("SAYDIN_DATA_REPAIR_KMS_KEY_VERSION_ID"),
    }
    for name, mutate in mutations.items():
        candidate = copy.deepcopy(baseline)
        mutate(candidate)
        if not validator.validate(candidate):
            print(f"production_validation_self_test_failed:{name}", file=sys.stderr)
            return 2
    print(f"production_validation_self_test_passed:{len(mutations)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
