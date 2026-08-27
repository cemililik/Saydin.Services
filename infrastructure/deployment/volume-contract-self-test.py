#!/usr/bin/env python3
"""Mutation tests for runtime volume ownership and blackbox target contracts."""

from __future__ import annotations

import importlib.util
import json
import os
import tempfile
from pathlib import Path


# Cloud link-local metadata endpoint. It appears here only as a mutation the blackbox
# target validator must reject; it is never dialled by this self-test.
LINK_LOCAL_METADATA_HOST = "169.254.169.254"  # NOSONAR


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError("validator_load_failed")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    directory = Path(__file__).resolve().parent
    runtime = load("runtime_volume_validator", directory / "validate-runtime-volume.py")
    blackbox = load("blackbox_target_validator", directory / "validate-blackbox-targets.py")
    with tempfile.TemporaryDirectory(prefix="saydin-volume-contract-", dir=directory) as temporary:
        workspace = Path(temporary)
        volume = workspace / "runtime"
        volume.mkdir(mode=0o700)
        if runtime.validate(volume, os.getuid()) is not None:
            raise SystemExit("volume_contract_self_test_failed:runtime_baseline")
        volume.chmod(0o755)
        if runtime.validate(volume, os.getuid()) is None:
            raise SystemExit("volume_contract_self_test_failed:runtime_mode")

        targets = workspace / "targets"
        targets.mkdir(mode=0o700)
        target_file = targets / "blackbox.json"
        target_file.write_text(json.dumps([{
            "targets": ["https://api.validation.test/health/live"],
            "labels": {"service": "saydin-edge"},
        }]), encoding="utf-8")
        target_file.chmod(0o600)
        if not blackbox.validate(targets, "api.validation.test", os.getuid()):
            raise SystemExit("volume_contract_self_test_failed:blackbox_baseline")
        target_file.write_text(json.dumps([{
            "targets": [f"http://{LINK_LOCAL_METADATA_HOST}/latest/meta-data"],  # NOSONAR
            "labels": {"service": "saydin-edge"},
        }]), encoding="utf-8")
        if blackbox.validate(targets, "api.validation.test", os.getuid()):
            raise SystemExit("volume_contract_self_test_failed:blackbox_arbitrary_target")
    print("volume_contract_self_test_passed:2")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
