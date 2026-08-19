#!/usr/bin/env python3
"""Static policy validation for release/deploy workflows and shell artifacts."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = (
    "release-images.yml", "deploy-staging.yml", "promote-production.yml",
    "rollback-production.yml", "restore-drill.yml",
)
ACTION = re.compile(r"uses:\s+[^\s]+@([0-9a-f]{40})(?:\s|$)")
UNPINNED_USES = re.compile(r"uses:\s+[^\s]+@(?![0-9a-f]{40}(?:\s|$))")
FORBIDDEN = re.compile(r"(?i)(PGPASSWORD=|DATABASE_URL=|POSTGRES_PASSWORD=|--password(?:=|\s))")


def main() -> int:
    errors: list[str] = []
    for name in WORKFLOWS:
        path = ROOT / ".github/workflows" / name
        if not path.is_file():
            errors.append(f"workflow_missing:{name}")
            continue
        text = path.read_text(encoding="utf-8")
        if UNPINNED_USES.search(text):
            errors.append(f"action_not_sha_pinned:{name}")
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
                "oci-kms-instance-principal", "--evidence-public-key", "verify-evidence"):
            if required not in text:
                errors.append(f"deployment_gate_missing:{required}")
    if errors:
        print("\n".join(sorted(errors)), file=sys.stderr)
        return 2
    print("release_static_validation_passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
