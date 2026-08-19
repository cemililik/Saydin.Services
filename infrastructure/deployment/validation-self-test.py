#!/usr/bin/env python3
"""Mutation tests for the production manifest validator."""

from __future__ import annotations

import copy
import importlib.util
import json
import sys
from pathlib import Path


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
        "raw_secret": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("REDIS_PASSWORD", "sentinel-secret"),
        "mutable_image": lambda value: value["services"]["saydin-api"].__setitem__("image", "ghcr.io/example/saydin-api:latest"),
        "internal_port": lambda value: value["services"]["postgres"].__setitem__("ports", [{"target": 5432, "published": "5432"}]),
        "read_write_root": lambda value: value["services"]["saydin-api"].__setitem__("read_only", False),
        "missing_cap_drop": lambda value: value["services"]["saydin-api"].__setitem__("cap_drop", []),
        "placeholder": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("SAYDIN_DEPLOYMENT_ID", "CHANGE_ME"),
        "unbounded_replication_slot": lambda value: value["services"]["postgres"].__setitem__("command", ["postgres"]),
        "backup_fetch_wal_window_missing": lambda value: value["services"]["postgres"].__setitem__(
            "command", [item for item in value["services"]["postgres"]["command"]
                        if item != "wal_keep_size=8GB"]),
        "missing_trace_backend": lambda value: value["services"].pop("tempo"),
        "telemetry_public_port": lambda value: value["services"]["loki"].__setitem__("ports", [{"target": 3100, "published": "3100"}]),
        "telemetry_ephemeral": lambda value: value["services"]["otel-collector"].__setitem__("volumes", []),
        "collapsed_api_ports": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("ApiRuntime__ManagementPort", "8080"),
        "readiness_as_liveness": lambda value: value["services"]["saydin-api"]["healthcheck"].__setitem__("test", ["CMD", "curl", "-fsS", "-H", "Host: saydin-api", "http://127.0.0.1:8080/health/ready"]),
        "liveness_host_missing": lambda value: value["services"]["saydin-api"]["healthcheck"].__setitem__("test", ["CMD", "curl", "-fsS", "http://127.0.0.1:8080/health/live"]),
        "service_version_missing": lambda value: value["services"]["saydin-api"]["environment"].pop("SAYDIN_SERVICE_VERSION"),
        "service_version_unbound": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("SAYDIN_SERVICE_VERSION", "different-release"),
        "malformed_host": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("AllowedHosts", "https://api.validation.test"),
        "broad_proxy_cidr": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("ForwardedHeaders__KnownNetworks", "172.16.0.0/12"),
        "proxy_forward_limit": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("ForwardedHeaders__ForwardLimit", "2"),
        "api_network_scope": lambda value: value["services"]["saydin-api"].__setitem__("networks", ["app", "data"]),
        "backup_plaintext": lambda value: value["services"]["database-backup"]["environment"].__setitem__("PGSSLMODE", "Disable"),
        "backup_broad_network": lambda value: value["networks"]["backup-db"]["ipam"]["config"][0].__setitem__("subnet", "172.16.0.0/12"),
        "backup_on_app_data": lambda value: value["services"]["database-backup"].__setitem__("networks", ["data", "backup-egress"]),
        "dqa_private_key": lambda value: value["services"]["data-quality-audit"]["command"].extend(["--evidence-private-key", "/run/private/key.pem"]),
        "dqa_no_kms_egress": lambda value: value["services"]["data-quality-audit"].__setitem__("networks", ["data"]),
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
