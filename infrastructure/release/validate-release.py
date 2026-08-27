#!/usr/bin/env python3
"""Static policy validation for release/deploy workflows and shell artifacts."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
RELEASE_WORKFLOWS = (
    "release-images.yml", "deploy-staging.yml", "promote-production.yml",
    "rollback-production.yml", "restore-drill.yml",
)
ACTION = re.compile(r"^[ \t]*(?:-[ \t]*)?uses:[ \t]*([^\s#]+)", re.M)
PINNED = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)?@[0-9a-f]{40}$")
FORBIDDEN = re.compile(r"(?i)(PGPASSWORD=|DATABASE_URL=|POSTGRES_PASSWORD=|--password[=\s])")


def main() -> int:
    errors: list[str] = []
    workflow_root = ROOT / ".github/workflows"
    workflows = sorted({*workflow_root.glob("*.yml"), *workflow_root.glob("*.yaml")})
    if not workflows:
        errors.append("workflow_missing")
    for path in workflows:
        text = path.read_text(encoding="utf-8")
        for reference in ACTION.findall(text):
            if reference.startswith("./"):
                continue
            if not PINNED.fullmatch(reference):
                errors.append(f"action_not_sha_pinned:{path.name}:{reference}")
    for name in RELEASE_WORKFLOWS:
        path = ROOT / ".github/workflows" / name
        if not path.is_file():
            errors.append(f"workflow_missing:{name}")
            continue
        text = path.read_text(encoding="utf-8")
        if FORBIDDEN.search(text):
            errors.append(f"raw_secret_in_workflow:{name}")
        if "self-hosted" not in text:
            errors.append(f"self_hosted_runner_missing:{name}")
        if "permissions:" not in text:
            errors.append(f"permissions_missing:{name}")
        expected_ref = f"$GITHUB_REPOSITORY/.github/workflows/{name}@refs/heads/main"
        if expected_ref not in text:
            errors.append(f"workflow_main_identity_gate_missing:{name}")
    release = (ROOT / ".github/workflows/release-images.yml")
    if release.is_file():
        text = release.read_text(encoding="utf-8")
        for required in ("linux/amd64,linux/arm64", "cosign sign", "spdx-json", "cyclonedx-json", "trivy"):
            if required not in text:
                errors.append(f"release_gate_missing:{required}")
        if not all(token in text for token in (
                "name: data_repair", "image: saydin-data-repair",
                "dockerfile: src/Saydin.DataRepair/Dockerfile")):
            errors.append("data_repair_release_matrix_missing")
        for token in ("actions: read", "verify-release-ci-admission.py", "ci-runs.json",
                      "ci-jobs.json"):
            if token not in text:
                errors.append(f"release_ci_admission_missing:{token}")
    manifest_tool = ROOT / "infrastructure/release/release_manifest.py"
    if manifest_tool.is_file():
        text = manifest_tool.read_text(encoding="utf-8")
        for token in ('"data_repair": "SAYDIN_DATA_REPAIR_IMAGE"',
                      "EXTERNAL_RUNTIME_IMAGES", "data_repair_runtime_image_mismatch"):
            if token not in text:
                errors.append(f"data_repair_runtime_authority_missing:{token}")
    production_compose = ROOT / "infrastructure/deployment/compose.production.yml"
    if production_compose.is_file():
        text = production_compose.read_text(encoding="utf-8")
        for token in ("  data-repair:", "profiles: [data-repair-operator]",
                      "command: [operator-command-required]",
                      "SAYDIN_DATA_REPAIR_IMAGE", "data_repair_receipts",
                      "data-repair-kms-egress"):
            if token not in text:
                errors.append(f"data_repair_production_contract_missing:{token}")
    if not (ROOT / "docs/runbooks/data-repair.md").is_file():
        errors.append("data_repair_runbook_missing")
    for path in list((ROOT / "infrastructure/release").glob("*.sh")) + list((ROOT / "infrastructure/backup").glob("*.sh")):
        text = path.read_text(encoding="utf-8")
        if "set -eu" not in text:
            errors.append(f"shell_fail_closed_missing:{path.name}")
    rollback = ROOT / "infrastructure/release/rollback-release.sh"
    if rollback.is_file():
        text = rollback.read_text(encoding="utf-8")
        for required in ("application-only", "verify-rollback", "verify-signed-release.sh",
                         "rollback_current_image_mismatch", "recover_current"):
            if required not in text:
                errors.append(f"rollback_gate_missing:{required}")
    deploy = ROOT / "infrastructure/release/deploy-release.sh"
    if deploy.is_file():
        text = deploy.read_text(encoding="utf-8")
        for required in (
                "manage_backup_hba.py", "prebootstrap_phase", "migration_phase",
                "backup_postbootstrap_required=false", "pg_reload_conf", "verify-auth",
                "oci-kms-instance-principal", "--evidence-public-key", "verify-evidence",
                "render-deployment-env.py", "--verify-existing",
                "validate-private-material.py", "validate-runtime-volume.py",
                "validate-blackbox-targets.py", "promtool prometheus",
                "amtool alertmanager", "--force-recreate",
                "validate-prometheus-runtime.py", "deployment_monitoring_readiness_failed"):
            if required not in text:
                errors.append(f"deployment_gate_missing:{required}")
        preflight = "compose run --rm --no-deps --entrypoint promtool prometheus"
        recreate = "compose up -d --no-deps --force-recreate"
        runtime_gate = "validate-prometheus-runtime.py"
        receipt = '"status": "passed"'
        if not all(token in text for token in (preflight, recreate, runtime_gate, receipt)) \
                or not (text.index(preflight) < text.index(recreate)
                        < text.rindex(runtime_gate) < text.index(receipt)):
            errors.append("deployment_monitoring_admission_order_invalid")
        wal_start = "compose --profile backup up -d --no-deps database-wal-archive"
        immediate_base = "compose --profile backup run --rm --no-deps database-backup base-backup"
        scheduler_start = "compose --profile backup up -d --no-deps database-backup"
        if not all(value in text for value in (wal_start, immediate_base, scheduler_start)) \
                or not (text.index(wal_start) < text.index(immediate_base) < text.index(scheduler_start)):
            errors.append("deployment_backup_start_sequence_invalid")
        if "runtime={" in text or "RUNTIME_IMAGE_ENV_KEYS" in text:
            errors.append("deployment_runtime_image_mapping_duplicated")
        if re.search(r"\bcompose\b[^\n]*\bdata-repair\b", text):
            errors.append("deployment_data_repair_automatic_start_forbidden")
        validity_tokens = (
            '"deployment_backup_validity_window_unsafe"', '3888000', '8035200',
            "FROM pg_catalog.pg_roles WHERE rolname=:'backup_role'",
            'deployment_backup_role_validity_mismatch',
            'SAYDIN_BACKUP_ROLE_VALID_UNTIL_EPOCH_SECONDS',
        )
        if not all(value in text for value in validity_tokens):
            errors.append("deployment_backup_validity_contract_missing")
        if all(value in text for value in ("deployment_postbootstrap_phase_invalid",
                                             "FROM pg_catalog.pg_roles",
                                             "database-wal-archive")) and not (
                text.index("deployment_postbootstrap_phase_invalid")
                < text.index("FROM pg_catalog.pg_roles")
                < text.index("database-wal-archive")):
            errors.append("deployment_backup_validity_order_invalid")
    restore_workflow = ROOT / ".github/workflows/restore-drill.yml"
    promote_workflow = ROOT / ".github/workflows/promote-production.yml"
    if restore_workflow.is_file():
        text = restore_workflow.read_text(encoding="utf-8")
        for token in ("schedule:", "17 2 1,15 * *", "workflow_dispatch:",
                      "SAYDIN_RESTORE_SCHEDULE_RELEASE_TAG", "github.run_attempt",
                      "steps.normalized.outputs.recovery_target"):
            if token not in text:
                errors.append(f"restore_workflow_contract_missing:{token}")
    if promote_workflow.is_file():
        text = promote_workflow.read_text(encoding="utf-8")
        for token in ("restore_drill_run_attempt", 'receipt["schemaVersion"]==2',
                      "walSegmentSourceAt", "walCoverageObservedAt",
                      "walSnapshotReceivedAt", "guaranteedRecoveryPointAt",
                      "walEvidenceEvaluatedAt", "currentRecoveryPointAgeSeconds",
                      "walReceiverCaughtUpAt", "walServerHighwaterSegment",
                      "recoveryTargetReached"):
            if token not in text:
                errors.append(f"promotion_restore_receipt_contract_missing:{token}")
        if '"recoveryLagSeconds"' in text or '"recoveredAt"' in text:
            errors.append("promotion_transaction_rpo_gate_forbidden")
    for relative in ("infrastructure/backup/restore-drill.sh",
                     "infrastructure/release/rollback-release.sh"):
        path = ROOT / relative
        if path.is_file():
            text = path.read_text(encoding="utf-8")
            if "script_dir=$(CDPATH=" not in text or "repo_root=$(CDPATH=" not in text:
                errors.append(f"script_root_resolution_missing:{path.name}")
    if errors:
        print("\n".join(sorted(errors)), file=sys.stderr)
        return 2
    print("release_static_validation_passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
