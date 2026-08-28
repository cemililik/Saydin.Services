#!/usr/bin/env python3
"""Validate project isolation, loopback exposure and dev-only tooling profiles."""

from __future__ import annotations

import copy
import json
import subprocess
import sys
from pathlib import Path


DEVTOOLS = {"pgadmin", "redis-insight", "aspire-dashboard", "prometheus"}
POST_BOOTSTRAP_CONSUMERS = {
    "pgadmin", "saydin-api", "saydin-price-ingestion",
    "postgres-exporter", "calendar-release",
}
SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c"
LOCAL_TEST_ENTRYPOINT = "/src/.github/scripts/run-local-tests.sh"
# Container-local heartbeat path asserted against docker-compose.yml. The validator
# never opens it — it only checks that the ingestion service declares this prefix.
INGESTION_HEARTBEAT_PREFIX = "/tmp/saydin-ingestion-"  # NOSONAR


def escape_identity_query_delimiter(document: dict) -> None:
    service = document["services"]["database-identity"]
    service["command"] = [
        str(value).replace('-c "SELECT', '-c \\"SELECT')
        for value in service.get("command") or []
    ]


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
    if not heartbeat.startswith(INGESTION_HEARTBEAT_PREFIX) or "$LivenessProbe__HeartbeatPath" not in heartbeat_test:
        errors.append("ingestion_heartbeat_contract")
    tests = services.get("tests") or {}
    if str(tests.get("image", "")) != SDK_IMAGE:
        errors.append("test_sdk_digest_contract")
    if tests.get("entrypoint") != [LOCAL_TEST_ENTRYPOINT] or tests.get("command") not in (None, []):
        errors.append("local_test_scope_contract")
    if tests.get("depends_on") or "ConnectionStrings__Redis" in (tests.get("environment") or {}):
        errors.append("local_unit_test_infrastructure_dependency")

    source_command = " ".join(str(value) for value in
                              (services.get("secret-source-generator") or {}).get("command") or [])
    materializer_command = " ".join(str(value) for value in
                                    (services.get("secret-materializer") or {}).get("command") or [])
    if "backup-v1" not in source_command:
        errors.append("backup_source_secret_missing")
    if "/out-bootstrap/private/backup-v1" not in materializer_command:
        errors.append("backup_bootstrap_secret_missing")

    identity_command = "\n".join(str(value) for value in
                                 (services.get("database-identity") or {}).get("command") or [])
    runtime_identity_command = identity_command.replace("$$", "$")
    syntax = subprocess.run(
        ["/bin/sh", "-n"], input=runtime_identity_command, text=True,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    if syntax.returncode != 0:
        errors.append("database_identity_shell_syntax_invalid")
    backup_validity_query = (
        "-c \"SELECT to_char(((clock_timestamp() AT TIME ZONE 'UTC')::date + 60)::timestamp, "
        "'YYYY-MM-DD\\\"T\\\"HH24:MI:SS\\\"Z\\\"')\"")
    if backup_validity_query not in identity_command \
            or "SAYDIN_BACKUP_V1_VALID_UNTIL=" not in identity_command:
        errors.append("database_identity_backup_validity_contract")

    bootstrap = services.get("database-role-bootstrap") or {}
    post_bootstrap = services.get("database-role-bootstrap-post-migration") or {}
    expected_backup_arguments = {
        "--backup-v1-valid-until": "2099-01-01T00:00:00Z",
        "--backup-password-file": "/run/saydin-secrets/private/backup-v1",
    }
    for service_name, service in (
            ("database-role-bootstrap", bootstrap),
            ("database-role-bootstrap-post-migration", post_bootstrap)):
        command = [str(value) for value in service.get("command") or []]
        for argument, expected_value in expected_backup_arguments.items():
            try:
                position = command.index(argument)
            except ValueError:
                errors.append(f"role_bootstrap_argument_missing:{service_name}:{argument}")
                continue
            if position + 1 >= len(command) or command[position + 1] != expected_value:
                errors.append(f"role_bootstrap_argument_invalid:{service_name}:{argument}")
    post_dependencies = post_bootstrap.get("depends_on") or {}
    bootstrap_dependencies = bootstrap.get("depends_on") or {}
    if (bootstrap_dependencies.get("database-backup-hba") or {}).get("condition") \
            != "service_completed_successfully":
        errors.append("pre_migration_backup_hba_gate_missing")
    if (post_dependencies.get("database-migrator") or {}).get("condition") \
            != "service_completed_successfully":
        errors.append("post_migration_bootstrap_gate_missing")
    if (post_dependencies.get("database-backup-hba") or {}).get("condition") \
            != "service_completed_successfully":
        errors.append("post_migration_backup_hba_gate_missing")
    backup_hba = services.get("database-backup-hba") or {}
    backup_hba_command = "\n".join(str(value) for value in backup_hba.get("command") or [])
    backup_hba_volumes = backup_hba.get("volumes") or []
    hba_data_mount = any(
        isinstance(value, dict)
        and value.get("source") == "postgres_data"
        and value.get("target") == "/var/lib/postgresql/data"
        for value in backup_hba_volumes)
    if str(backup_hba.get("user")) != "70:70" \
            or backup_hba.get("read_only") is not True \
            or backup_hba.get("cap_drop") != ["ALL"] \
            or "manage-backup-hba.py install" not in backup_hba_command \
            or "manage-backup-hba.py verify" not in backup_hba_command \
            or "--fixture-cleartext" not in backup_hba_command \
            or "--role-prefix \"$$SAYDIN_DATABASE_ROLE_PREFIX\"" not in backup_hba_command \
            or "pg_reload_conf" not in backup_hba_command \
            or not hba_data_mount:
        errors.append("development_backup_hba_contract_invalid")
    for consumer_name in POST_BOOTSTRAP_CONSUMERS:
        consumer_dependencies = (services.get(consumer_name) or {}).get("depends_on") or {}
        if (consumer_dependencies.get("database-role-bootstrap-post-migration") or {}).get("condition") \
                != "service_completed_successfully":
            errors.append(f"post_migration_bootstrap_consumer_bypass:{consumer_name}")
    migrator_environment = (services.get("database-migrator") or {}).get("environment") or {}
    if str(migrator_environment.get("SAYDIN_BACKUP_V1_VALID_UNTIL")) != "2099-01-01T00:00:00Z":
        errors.append("migrator_backup_validity_missing")
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
        "broad_local_test_scope": lambda value: value["services"]["tests"].__setitem__(
            "entrypoint", ["dotnet", "test", "Saydin.Services.sln"]),
        "unit_tests_require_infrastructure": lambda value: value["services"]["tests"].update({
            "depends_on": {"redis": {"condition": "service_healthy"}},
            "environment": {"ConnectionStrings__Redis": "redis:6379"},
        }),
        "missing_backup_secret": lambda value: value["services"]["secret-source-generator"].__setitem__("command", ["true"]),
        "invalid_identity_shell": lambda value: value["services"]["database-identity"].__setitem__("command", ["broken=("]),
        "escaped_identity_query_delimiter": escape_identity_query_delimiter,
        "missing_identity_backup_validity": lambda value: value["services"]["database-identity"].__setitem__("command", ["true"]),
        "missing_backup_argument": lambda value: value["services"]["database-role-bootstrap"].__setitem__("command", ["ensure"]),
        "missing_post_bootstrap": lambda value: value["services"].pop("database-role-bootstrap-post-migration"),
        "post_bootstrap_bypasses_migrator": lambda value: value["services"]["database-role-bootstrap-post-migration"].__setitem__("depends_on", {}),
        "post_bootstrap_bypasses_hba": lambda value: value["services"]["database-role-bootstrap-post-migration"]["depends_on"].pop("database-backup-hba"),
        "pre_bootstrap_bypasses_hba": lambda value: value["services"]["database-role-bootstrap"]["depends_on"].pop("database-backup-hba"),
        "broad_backup_hba": lambda value: value["services"]["database-backup-hba"].__setitem__("command", ["host replication all 0.0.0.0/0 trust"]),
        "api_bypasses_post_bootstrap": lambda value: value["services"]["saydin-api"].__setitem__("depends_on", {"database-migrator": {"condition": "service_completed_successfully"}}),
        "missing_migrator_backup_validity": lambda value: value["services"]["database-migrator"]["environment"].pop("SAYDIN_BACKUP_V1_VALID_UNTIL"),
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
