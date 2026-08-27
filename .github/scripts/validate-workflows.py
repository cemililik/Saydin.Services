#!/usr/bin/env python3
"""Reject mutable action references and missing required assurance jobs."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ACTION = re.compile(r"^[ \t]*(?:-[ \t]*)?uses:[ \t]*([^\s#]+)", re.M)
PINNED = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)?@[0-9a-f]{40}$")


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    workflow_root = root / ".github" / "workflows"
    workflows = sorted({*workflow_root.glob("*.yml"), *workflow_root.glob("*.yaml")})
    errors: list[str] = []

    try:
        solution = (root / "Saydin.Services.sln").read_text(encoding="utf-8-sig")
    except OSError:
        errors.append("solution_missing")
        solution = ""
    solution_projects = {
        value.replace("\\", "/")
        for value in re.findall(r'^Project\("[^"]+"\) = "[^"]+", "([^"]+\.csproj)"',
                                solution, re.M)
    }
    repository_projects = {
        path.relative_to(root).as_posix()
        for path in root.rglob("*.csproj")
        if not {".git", "bin", "obj"}.intersection(path.relative_to(root).parts)
    }
    for missing in sorted(repository_projects - solution_projects):
        errors.append(f"solution_project_missing:{missing}")
    for stale in sorted(solution_projects - repository_projects):
        errors.append(f"solution_project_stale:{stale}")

    if not workflows:
        errors.append("workflow_missing")
    for path in workflows:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            errors.append(f"workflow_unreadable:{path.name}")
            continue
        for reference in ACTION.findall(text):
            if reference.startswith("./"):
                continue
            if not PINNED.fullmatch(reference):
                errors.append(f"action_not_full_sha:{path.name}:{reference}")
    try:
        ci = (root / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    except OSError:
        errors.append("ci_missing")
        ci = ""
    for job in (
        "build-and-test",
        "integration-test",
        "coverage-admission",
        "production-assurance",
        "supply-chain",
        "codeql",
        "docker-build",
    ):
        if not re.search(rf"^  {re.escape(job)}:\s*$", ci, re.M):
            errors.append(f"required_job_missing:{job}")
    for token in (
        "data-repair-tests",
        ".github/scripts/run-development-compose-smoke.sh",
        '[[ "${#unit_reports[@]}" -eq "$expected_unit_reports" ]]',
        '[[ "${#integration_reports[@]}" -eq "$expected_integration_reports" ]]',
    ):
        if token not in ci:
            errors.append(f"required_test_admission_missing:{token}")
    try:
        local_test_runner = (root / ".github/scripts/run-local-tests.sh").read_text(
            encoding="utf-8")
    except OSError:
        local_test_runner = ""
    if "local_test_scope_rejected:explicit_project_required" not in local_test_runner:
        errors.append("local_test_explicit_project_guard_missing")
    for trx, minimum in {
        "integration.trx": 66,
        "calendar-data.trx": 94,
        "ingestion-ledger.trx": 44,
        "data-quality-audit-unit.trx": 97,
        "data-quality-audit-integration.trx": 106,
        "data-repair-integration.trx": 32,
        "role-bootstrap-unit.trx": 98,
        "role-bootstrap-integration.trx": 13,
        "migrator.trx": 185,
    }.items():
        pattern = rf'{re.escape(trx)}"?\s*\\\s*--minimum-executed\s+{minimum}\b'
        if not re.search(pattern, ci):
            errors.append(f"required_test_admission_missing:{trx}:{minimum}")
    expected_migrations = len([
        path for path in (root / "infrastructure/postgres/migrations").iterdir()
        if path.suffix in {".sql", ".sh"}
    ])
    expected_schema_state = (
        f"{expected_migrations},2,{expected_migrations},{expected_migrations},ready")
    if ci.count(f'== "{expected_schema_state}"') != 4:
        errors.append(
            f"fresh_schema_ratchet_missing:{expected_schema_state}:targets=4")
    expected_summary = (
        f"targets=4 migrations={expected_migrations} hypertables=2 "
        f"checksums={expected_migrations} terminal={expected_migrations} control=ready")
    if expected_summary not in ci:
        errors.append(f"fresh_schema_summary_stale:{expected_migrations}")
    try:
        integration_compose = (root / ".github" / "compose.integration.yml").read_text(
            encoding="utf-8")
    except OSError:
        errors.append("integration_compose_missing")
        integration_compose = ""
    for service in (
        "database-migrator",
        "ingestion-database-migrator",
        "data-quality-audit-database-migrator",
        "data-repair-database-migrator",
    ):
        match = re.search(
            rf"^  {re.escape(service)}:\s*$(.*?)(?=^  [a-z0-9][a-z0-9-]*:\s*$|\Z)",
            integration_compose,
            re.M | re.S,
        )
        if match is None or len(re.findall(r"^ {6}PGSSLMODE: Disable[ \t]*$", match.group(1), re.M)) != 1:
            errors.append(f"integration_migrator_sslmode_missing:{service}:Disable")
    try:
        unit_runner = (root / ".github" / "scripts" / "run-unit-coverage.sh").read_text(
            encoding="utf-8")
    except OSError:
        errors.append("unit_coverage_runner_missing")
        unit_runner = ""
    if "minimum_tests=(658 182 97 78 98 29 94)" not in unit_runner:
        errors.append("unit_test_ratchet_missing:658,182,97,78,98,29,94")
    try:
        backup_static = (
            root / "infrastructure" / "backup" / "tests" / "backup-static-self-test.py"
        ).read_text(encoding="utf-8")
    except OSError:
        backup_static = ""
        errors.append("backup_static_self_test_missing")
    for token in (
        "EXPECTED_CHECK_COUNT = 64",
        'os.environ.get("SAYDIN_REQUIRE_DOCKER_SMOKES", "0")',
        "backup_static_failed:check_count",
    ):
        if token not in backup_static:
            errors.append(f"backup_static_ratchet_missing:{token}")
    for workflow_name in ("ci.yml", "release-images.yml"):
        try:
            workflow_text = (workflow_root / workflow_name).read_text(encoding="utf-8")
        except OSError:
            continue
        if 'SAYDIN_REQUIRE_DOCKER_SMOKES: "1"' not in workflow_text:
            errors.append(f"backup_docker_smoke_required_missing:{workflow_name}")
    unit_projects = {
        path.relative_to(root).as_posix()
        for base in (root / "tests", root / "tools" / "calendar-data" / "tests")
        for path in base.rglob("*Tests.csproj")
        if not path.name.endswith("IntegrationTests.csproj")
    }
    configured_unit_projects = set(re.findall(r'"([^"\n]+Tests\.csproj)"', unit_runner))
    for missing in sorted(unit_projects - configured_unit_projects):
        errors.append(f"unit_project_admission_missing:{missing}")
    for stale in sorted(configured_unit_projects - unit_projects):
        errors.append(f"unit_project_admission_stale:{stale}")
    for token in (
        "unit_project_inventory_mismatch",
        ".github/scripts/verify-integration-trx.py",
        "git ls-files -z -- '*.sh'",
    ):
        if token not in unit_runner and token != "git ls-files -z -- '*.sh'":
            errors.append(f"unit_runner_gate_missing:{token}")
        if token == "git ls-files -z -- '*.sh'" and token not in ci:
            errors.append("tracked_shell_admission_missing")
    for relative in (
        ".github/scripts/run-development-compose-smoke.sh",
        "tests/Saydin.DataRepair.IntegrationTests/run-isolated.sh",
    ):
        try:
            script = (root / relative).read_text(encoding="utf-8")
        except OSError:
            errors.append(f"migration_count_consumer_missing:{relative}")
            continue
        if re.search(r"already_applied=[0-9]+", script):
            errors.append(f"migration_count_literal_forbidden:{relative}")
    try:
        restore = (root / ".github" / "workflows" / "restore-drill.yml").read_text(
            encoding="utf-8")
    except OSError:
        errors.append("restore_workflow_missing")
        restore = ""
    for token in ("schedule:", "workflow_dispatch:", "17 2 1,15 * *",
                  "SAYDIN_RESTORE_SCHEDULE_RELEASE_TAG", "github.run_attempt",
                  "steps.normalized.outputs.release_tag"):
        if token not in restore:
            errors.append(f"restore_workflow_gate_missing:{token}")
    if errors:
        for error in sorted(set(errors)):
            print(f"workflow_validation_failed:{error}", file=sys.stderr)
        return 2
    print(f"workflow_validation_passed:files={len(workflows)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
