#!/usr/bin/env python3
"""Mutation tests prove telemetry validation rejects missing and weakened artifacts."""

from __future__ import annotations

import importlib.util
import shutil
import sys
import tempfile
from pathlib import Path


def load_validator(path: Path):
    spec = importlib.util.spec_from_file_location("saydin_observability_validator", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("validator_load_failed")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    repo = Path(__file__).resolve().parents[2]
    validator = load_validator(Path(__file__).with_name("validate-observability.py"))
    if validator.validate(repo):
        print("observability_self_test_failed:baseline", file=sys.stderr)
        return 2
    mutations = {
        "missing_otel": ("infrastructure/otel/otel-collector.production.yml", None),
        "malformed_empty_loki": ("infrastructure/otel/loki.production.yml", ""),
        "nop_exporter": ("infrastructure/otel/otel-collector.production.yml", "\nexporters:\n  nop: {}\n"),
        "missing_queue": ("infrastructure/otel/otel-collector.production.yml", ("storage: file_storage", "storage: memory")),
        "missing_release_tag": ("infrastructure/otel/otel-collector.production.yml", ("service.version", "service.release")),
        "missing_tempo_retention": ("infrastructure/otel/tempo.production.yml", ("block_retention:", "block_lifetime:")),
        "missing_loki_retention": ("infrastructure/otel/loki.production.yml", ("retention_enabled: true", "retention_enabled: false")),
        "public_api_scrape": ("infrastructure/prometheus/prometheus.production.yml", ("saydin-api:9090", "saydin-api:8080")),
    }
    for name, (relative, change) in mutations.items():
        with tempfile.TemporaryDirectory(prefix="saydin-observability-") as directory:
            candidate = Path(directory)
            shutil.copytree(repo / "infrastructure", candidate / "infrastructure")
            shutil.copytree(repo / "docs" / "runbooks", candidate / "docs" / "runbooks")
            target = candidate / relative
            if change is None:
                target.unlink()
            elif change == "":
                target.write_text("", encoding="utf-8")
            elif isinstance(change, tuple):
                target.write_text(target.read_text(encoding="utf-8").replace(*change, 1), encoding="utf-8")
            else:
                target.write_text(target.read_text(encoding="utf-8") + change, encoding="utf-8")
            if not validator.validate(candidate):
                print(f"observability_self_test_failed:{name}", file=sys.stderr)
                return 2
    print(f"observability_self_test_passed:{len(mutations)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
