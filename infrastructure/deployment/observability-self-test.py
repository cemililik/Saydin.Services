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
        "missing_backup_validity_alert": ("infrastructure/prometheus/rules/host-backup.yml", ("SaydinBackupLoginExpiring", "BackupLoginWarningRemoved")),
        "block_style_pii_label": ("infrastructure/prometheus/rules/api.yml", (
            "labels: {severity: critical, service: saydin-api}",
            "labels:\n          severity: critical\n          service: saydin-api\n          device_id: forbidden")),
        "missing_negative_inventory": ("infrastructure/prometheus/tests/inventory.test.yml", (
            "alertname: SaydinApiUnavailable, exp_alerts: []",
            "alertname: RemovedApiUnavailableNegative, exp_alerts: []")),
        "missing_positive_inventory": ("infrastructure/prometheus/tests/inventory.test.yml", (
            "alertname: SaydinActivityLogLoss\n", "alertname: RemovedActivityLogLossPositive\n")),
        "stale_runbook_alert": ("docs/runbooks/observability-game-day.md", (
            "SaydinDailyIngestionStale", "SaydinNonexistentAlert")),
        "watchdog_removed": ("infrastructure/prometheus/rules/tls-runtime.yml", (
            "SaydinWatchdog", "RemovedWatchdog")),
        "instance_identity_overwrite": ("infrastructure/otel/otel-collector.production.yml", (
            "action: insert", "action: upsert")),
        "resource_label_explosion": ("infrastructure/otel/otel-collector.production.yml", (
            "enabled: false", "enabled: true")),
        "loopback_health_endpoint": ("infrastructure/otel/otel-collector.production.yml", (
            "endpoint: 0.0.0.0:13133", "endpoint: 127.0.0.1:13133")),
        "unbounded_metric_inventory": ("infrastructure/release/deploy-release.sh", (
            "&start=$series_start&end=$series_end", "")),
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
