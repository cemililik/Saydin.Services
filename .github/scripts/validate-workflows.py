#!/usr/bin/env python3
"""Reject mutable action references and missing required assurance jobs."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ACTION = re.compile(r"^\s*-?\s*uses:\s*([^\s#]+)", re.M)
PINNED = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)?@[0-9a-f]{40}$")


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    workflows = sorted((root / ".github" / "workflows").glob("*.yml"))
    errors: list[str] = []
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
        "data-repair-integration.trx",
        "--minimum-executed 7",
        '[[ "${#unit_reports[@]}" -eq 7 ]]',
        '[[ "${#integration_reports[@]}" -eq 5 ]]',
    ):
        if token not in ci:
            errors.append(f"data_repair_admission_missing:{token}")
    if errors:
        for error in sorted(set(errors)):
            print(f"workflow_validation_failed:{error}", file=sys.stderr)
        return 2
    print(f"workflow_validation_passed:files={len(workflows)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
