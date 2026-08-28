#!/usr/bin/env python3
"""Fail-closed fixtures for release CI admission."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile


SCRIPT = Path(__file__).with_name("verify-release-ci-admission.py")
SPEC = importlib.util.spec_from_file_location("release_ci_admission", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("release CI admission module could not be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value), encoding="utf-8")


def expect_failure(action) -> None:
    try:
        action()
    except ValueError:
        return
    raise AssertionError("fixture unexpectedly admitted")


def main() -> None:
    sha = "a" * 40
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        runs = root / "runs.json"
        jobs = root / "jobs.json"
        write(runs, {"workflow_runs": [{
            "id": 42, "head_sha": sha, "head_branch": "main", "event": "push",
            "status": "completed", "conclusion": "success",
        }]})
        assert MODULE.select_run(runs, sha, "main") == 42
        write(jobs, {"jobs": [{
            "name": name, "run_id": 42, "head_sha": sha,
            "status": "completed", "conclusion": "success",
        } for name in sorted(MODULE.REQUIRED_JOBS)]})
        MODULE.verify_jobs(jobs, sha, 42)

        value = json.loads(jobs.read_text(encoding="utf-8"))
        value["jobs"][0]["conclusion"] = "skipped"
        write(jobs, value)
        expect_failure(lambda: MODULE.verify_jobs(jobs, sha, 42))

        write(runs, {"workflow_runs": [{
            "id": 43, "head_sha": sha, "head_branch": "main", "event": "workflow_dispatch",
            "status": "completed", "conclusion": "success",
        }]})
        expect_failure(lambda: MODULE.select_run(runs, sha, "main"))
    print("release_ci_admission_self_test_passed")


if __name__ == "__main__":
    main()
