#!/usr/bin/env python3
"""Validate project isolation, loopback exposure and dev-only tooling profiles."""

from __future__ import annotations

import copy
import json
import sys
from pathlib import Path


DEVTOOLS = {"pgadmin", "redis-insight", "aspire-dashboard", "prometheus"}
SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c"


def validate(document: dict) -> list[str]:
    errors: list[str] = []
    services = document.get("services") or {}
    if not document.get("name"):
        errors.append("project_name_missing")
    for name, service in services.items():
        if service.get("container_name"):
            errors.append(f"fixed_container_name:{name}")
        for port in service.get("ports") or []:
            if not isinstance(port, dict) or str(port.get("host_ip")) != "127.0.0.1":
                errors.append(f"non_loopback_port:{name}")
    for name in DEVTOOLS:
        profiles = set((services.get(name) or {}).get("profiles") or [])
        if profiles != {"devtools"}:
            errors.append(f"devtools_profile_missing:{name}")

    api = services.get("saydin-api") or {}
    api_ports = [port for port in api.get("ports") or [] if isinstance(port, dict)]
    if len(api_ports) != 1 or str(api_ports[0].get("target")) != "8080":
        errors.append("api_port_contract")
    api_environment = api.get("environment") or {}
    if str(api_environment.get("ApiRuntime__PublicPort")) != "8080" \
            or str(api_environment.get("ApiRuntime__ManagementPort")) != "9090":
        errors.append("api_port_boundary_config")
    health = " ".join(str(value) for value in (api.get("healthcheck") or {}).get("test") or [])
    if "http://localhost:8080/health/live" not in health or "/ready" in health:
        errors.append("api_health_contract")

    ingestion = services.get("saydin-price-ingestion") or {}
    environment = ingestion.get("environment") or {}
    heartbeat = str(environment.get("LivenessProbe__HeartbeatPath", ""))
    heartbeat_test = " ".join(str(value) for value in (ingestion.get("healthcheck") or {}).get("test") or [])
    if not heartbeat.startswith("/tmp/saydin-ingestion-") or "$LivenessProbe__HeartbeatPath" not in heartbeat_test:
        errors.append("ingestion_heartbeat_contract")
    if str((services.get("tests") or {}).get("image", "")) != SDK_IMAGE:
        errors.append("test_sdk_digest_contract")
    return sorted(set(errors))


def main() -> int:
    if len(sys.argv) != 3:
        print("development_compose_validation_failed:two_documents_required", file=sys.stderr)
        return 64
    try:
        first = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
        second = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        print("development_compose_validation_failed:document_invalid", file=sys.stderr)
        return 2
    errors = validate(first) + validate(second)
    if first.get("name") == second.get("name"):
        errors.append("project_names_collide")
    first_volumes = {str((value or {}).get("name", "")) for value in (first.get("volumes") or {}).values()}
    second_volumes = {str((value or {}).get("name", "")) for value in (second.get("volumes") or {}).values()}
    if not first_volumes or not second_volumes or first_volumes.intersection(second_volumes):
        errors.append("project_volumes_collide")

    mutations = {
        "fixed_name": lambda value: value["services"]["saydin-api"].__setitem__("container_name", "saydin-api"),
        "public_api": lambda value: value["services"]["saydin-api"]["ports"][0].__setitem__("host_ip", "0.0.0.0"),
        "admin_default": lambda value: value["services"]["pgadmin"].__setitem__("profiles", []),
        "wrong_health": lambda value: value["services"]["saydin-api"]["healthcheck"].__setitem__("test", ["CMD", "curl", "http://localhost:8080/health/ready"]),
        "collapsed_api_ports": lambda value: value["services"]["saydin-api"]["environment"].__setitem__("ApiRuntime__ManagementPort", "8080"),
        "heartbeat_literal": lambda value: value["services"]["saydin-price-ingestion"]["healthcheck"].__setitem__("test", ["CMD-SHELL", "test -f /tmp/hard-coded"]),
        "mutable_test_sdk": lambda value: value["services"]["tests"].__setitem__("image", "mcr.microsoft.com/dotnet/sdk:10.0"),
    }
    for name, mutate in mutations.items():
        candidate = copy.deepcopy(first)
        mutate(candidate)
        if not validate(candidate):
            errors.append(f"mutation_false_clean:{name}")
    if errors:
        for error in sorted(set(errors)):
            print(f"development_compose_validation_failed:{error}", file=sys.stderr)
        return 2
    print(f"development_compose_validation_passed:mutations={len(mutations)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
