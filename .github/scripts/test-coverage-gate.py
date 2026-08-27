#!/usr/bin/env python3
"""Fail-closed smoke fixtures for coverage-gate.py."""

from __future__ import annotations

import subprocess
import sys
import tempfile
import importlib.util
from pathlib import Path


VALID = """<?xml version='1.0'?>
<coverage><packages><package name='Saydin.Api.Security'><classes>
<class name='Saydin.Api.Security.Guard' filename='src/Saydin.Api/Security/Guard.cs'><lines>
<line number='10' hits='1' branch='true' condition-coverage='100% (2/2)' />
</lines></class></classes></package></packages></coverage>
"""


def run(script: Path, report: Path, policy: Path) -> int:
    return subprocess.run(
        [sys.executable, str(script), "--reports", str(report), "--policy", str(policy)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    ).returncode


def main() -> int:
    script = Path(__file__).with_name("coverage-gate.py")
    with tempfile.TemporaryDirectory(prefix="saydin-coverage-fixtures-") as directory:
        root = Path(directory)
        policy = root / "policy.json"
        policy.write_text(
            '{"overall":{"line":100,"branch":100},"changed_line":80,'
            '"critical_namespaces":{"Saydin.Api.Security":{"line":100,"branch":100}}}',
            encoding="utf-8",
        )
        missing = root / "missing.xml"
        malformed = root / "malformed.xml"
        malformed.write_text("<coverage>", encoding="utf-8")
        under = root / "under.xml"
        under.write_text(VALID.replace("hits='1'", "hits='0'").replace("2/2", "0/2"), encoding="utf-8")
        valid = root / "valid.xml"
        valid.write_text(VALID, encoding="utf-8")
        outcomes = {
            "missing": run(script, missing, policy),
            "malformed": run(script, malformed, policy),
            "under_threshold": run(script, under, policy),
            "valid": run(script, valid, policy),
        }
        if outcomes["valid"] != 0 or any(outcomes[key] == 0 for key in ("missing", "malformed", "under_threshold")):
            print(f"coverage_gate_self_test_failed:{outcomes}", file=sys.stderr)
            return 2
        source = root / "src/Saydin.Api/Security/Guard.cs"
        source.parent.mkdir(parents=True)
        source.write_text("namespace Fixture;\n", encoding="utf-8")
        spec = importlib.util.spec_from_file_location("saydin_coverage_gate", script)
        if spec is None or spec.loader is None:
            print("coverage_gate_self_test_failed:load", file=sys.stderr)
            return 2
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)
        if module.normalize_path("/repo/src/Saydin.Api/Security/Guard.cs", root) != \
                "src/Saydin.Api/Security/Guard.cs":
            print("coverage_gate_self_test_failed:container_path", file=sys.stderr)
            return 2
    print("coverage_gate_self_test_passed:5")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
