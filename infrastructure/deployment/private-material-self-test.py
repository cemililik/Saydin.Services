#!/usr/bin/env python3
"""Mutation tests for the pre-materialized secret validator."""

from __future__ import annotations

import importlib.util
import os
import shutil
import tempfile
from pathlib import Path


def load_validator(path: Path):
    spec = importlib.util.spec_from_file_location("private_material_validator", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("validator_load_failed")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def materialize(root: Path) -> None:
    root.mkdir(mode=0o700)
    value = root / "secret"
    value.write_text("a" * 32, encoding="utf-8")
    value.chmod(0o600)


def main() -> int:
    validator = load_validator(Path(__file__).with_name("validate-private-material.py"))
    expected = {"secret": "scalar"}
    with tempfile.TemporaryDirectory(
            prefix="saydin-private-material-", dir=Path(__file__).resolve().parent) as directory:
        workspace = Path(directory)
        baseline = workspace / "baseline"
        materialize(baseline)
        if validator.validate_material(baseline, os.getuid(), expected) is not None:
            raise SystemExit("private_material_self_test_failed:baseline")

        mutations = {
            "world_readable_root": lambda root: root.chmod(0o755),
            "world_readable_file": lambda root: (root / "secret").chmod(0o644),
            "placeholder": lambda root: (root / "secret").write_text("CHANGE_ME" * 4, encoding="utf-8"),
            "extra_file": lambda root: (root / "extra").write_text("x", encoding="utf-8"),
            "symlink_file": lambda root: ((root / "secret").unlink(),
                                            (root / "secret").symlink_to("/dev/null")),
        }
        for name, mutate in mutations.items():
            candidate = workspace / name
            shutil.copytree(baseline, candidate)
            mutate(candidate)
            if validator.validate_material(candidate, os.getuid(), expected) is None:
                raise SystemExit(f"private_material_self_test_failed:{name}")
        alert_root = workspace / "alertmanager"
        alert_root.mkdir(mode=0o700)
        alert_file = alert_root / "alertmanager.yml"
        alert_baseline = (
            "route:\n"
            "  routes:\n"
            "    - matchers: ['severity=\"watchdog\"']\n"
            "      receiver: external-watchdog\n"
            "      repeat_interval: 1m\n"
            "receivers:\n"
            "  - name: operator-critical\n"
            "    webhook_configs:\n"
            "      - url: https://alerts.valid/critical\n"
            "        send_resolved: true\n"
            "  - name: operator-warning\n"
            "    webhook_configs:\n"
            "      - url: https://alerts.valid/warning\n"
            "        send_resolved: true\n"
            "  - name: external-watchdog\n"
            "    webhook_configs:\n"
            "      - url: https://heartbeat.valid/saydin\n"
            "        send_resolved: true\n"
        )
        alert_file.write_text(alert_baseline, encoding="utf-8")
        alert_file.chmod(0o600)
        alert_expected = {"alertmanager.yml": "alertmanager"}
        if validator.validate_material(alert_root, os.getuid(), alert_expected) is not None:
            raise SystemExit("private_material_self_test_failed:alertmanager_baseline")
        alert_file.write_text(
            alert_file.read_text(encoding="utf-8").replace(
                "https://heartbeat.valid/saydin", "https://example.invalid/CHANGE_ME"),
            encoding="utf-8")
        if validator.validate_material(alert_root, os.getuid(), alert_expected) is None:
            raise SystemExit("private_material_self_test_failed:alertmanager_placeholder")
        alert_mutations = {
            "alertmanager_wrong_watchdog_receiver": (
                "receiver: external-watchdog", "receiver: operator-critical"),
            "alertmanager_slow_watchdog": ("repeat_interval: 1m", "repeat_interval: 5m"),
            "alertmanager_watchdog_without_resolve": (
                "https://heartbeat.valid/saydin\n        send_resolved: true",
                "https://heartbeat.valid/saydin\n        send_resolved: false"),
            "alertmanager_shared_watchdog_host": (
                "https://heartbeat.valid/saydin", "https://alerts.valid/watchdog"),
        }
        for name, (old, new) in alert_mutations.items():
            alert_file.write_text(alert_baseline.replace(old, new, 1), encoding="utf-8")
            if validator.validate_material(alert_root, os.getuid(), alert_expected) is None:
                raise SystemExit(f"private_material_self_test_failed:{name}")
        repair_expected_uid, repair_expected = validator.EXPECTED.get("data-repair", (None, None))
        if repair_expected_uid != 1001 or repair_expected != {
                "ingestion-current": "scalar", "audit-current": "scalar"}:
            raise SystemExit("private_material_self_test_failed:data_repair_contract")
        repair_root = workspace / "data-repair"
        repair_root.mkdir(mode=0o700)
        for name in repair_expected:
            path = repair_root / name
            path.write_text("r" * 32, encoding="utf-8")
            path.chmod(0o600)
        if validator.validate_material(repair_root, os.getuid(), repair_expected) is not None:
            raise SystemExit("private_material_self_test_failed:data_repair_baseline")
    print(f"private_material_self_test_passed:{len(mutations) + len(alert_mutations) + 2}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
