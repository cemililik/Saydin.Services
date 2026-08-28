#!/usr/bin/env python3
"""Select and verify the successful required CI run for a release commit."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys


SHA = re.compile(r"^[0-9a-f]{40}$")
REQUIRED_JOBS = {
    "build-and-test",
    "Production render, observability and mutation gates",
    "Dependency, license, vulnerability, secret and IaC gates",
    "CodeQL C# SAST",
    "Integration tests (TimescaleDB + Redis)",
    "Merged unit and real-integration changed-line coverage",
    "docker-build",
}


def load_object(path: Path) -> dict[str, object]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"JSON object required: {path}")
    return value


def select_run(runs_path: Path, commit_sha: str, branch: str) -> int:
    value = load_object(runs_path)
    runs = value.get("workflow_runs")
    if not isinstance(runs, list):
        raise ValueError("workflow_runs array required")
    admitted: list[int] = []
    for run in runs:
        if not isinstance(run, dict):
            continue
        run_id = run.get("id")
        if (
            isinstance(run_id, int)
            and run.get("head_sha") == commit_sha
            and run.get("head_branch") == branch
            and run.get("event") == "push"
            and run.get("status") == "completed"
            and run.get("conclusion") == "success"
        ):
            admitted.append(run_id)
    if not admitted:
        raise ValueError("successful exact-commit main push CI run missing")
    return max(admitted)


def verify_jobs(jobs_path: Path, commit_sha: str, run_id: int) -> None:
    value = load_object(jobs_path)
    jobs = value.get("jobs")
    if not isinstance(jobs, list):
        raise ValueError("jobs array required")
    observed: dict[str, list[dict[str, object]]] = {}
    for job in jobs:
        if not isinstance(job, dict):
            continue
        name = job.get("name")
        if isinstance(name, str):
            observed.setdefault(name, []).append(job)
    for name in sorted(REQUIRED_JOBS):
        matches = observed.get(name, [])
        if len(matches) != 1:
            raise ValueError(f"required CI job cardinality invalid: {name}:{len(matches)}")
        job = matches[0]
        if job.get("run_id") != run_id:
            raise ValueError(f"required CI job run mismatch: {name}")
        if job.get("head_sha") != commit_sha:
            raise ValueError(f"required CI job commit mismatch: {name}")
        if job.get("status") != "completed" or job.get("conclusion") != "success":
            raise ValueError(f"required CI job not successful: {name}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    select = subparsers.add_parser("select")
    select.add_argument("--runs", type=Path, required=True)
    select.add_argument("--commit-sha", required=True)
    select.add_argument("--branch", default="main")
    verify = subparsers.add_parser("verify")
    verify.add_argument("--jobs", type=Path, required=True)
    verify.add_argument("--commit-sha", required=True)
    verify.add_argument("--run-id", type=int, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if not SHA.fullmatch(args.commit_sha):
        raise ValueError("commit SHA must be 40 lowercase hexadecimal characters")
    if args.command == "select":
        print(select_run(args.runs, args.commit_sha, args.branch))
    else:
        if args.run_id < 1:
            raise ValueError("run id must be positive")
        verify_jobs(args.jobs, args.commit_sha, args.run_id)
        print(f"release_ci_admission_passed:run_id={args.run_id}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError) as exc:
        print(f"release_ci_admission_failed:{exc}", file=sys.stderr)
        raise SystemExit(2) from exc
