#!/usr/bin/env python3
"""Bind live Prometheus rule/target state to the reviewed repository inventory."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path


EXPECTED_JOBS = {
    "saydin-api", "otel-collector", "otel-pipeline", "tempo", "loki",
    "postgres", "redis", "node", "blackbox-https", "prometheus", "alertmanager",
}


def load(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or value.get("status") != "success":
        raise ValueError("api_status")
    return value


def validate(rule_root: Path, rules_path: Path, targets_path: Path,
             series_path: Path, expected_probe: str,
             require_ingestion: bool = False) -> list[str]:
    errors: list[str] = []
    expected_alerts: set[str] = set()
    for path in sorted(rule_root.glob("*.yml")):
        expected_alerts.update(re.findall(
            r"^\s*- alert:\s+(\S+)", path.read_text(encoding="utf-8"), re.M))
    if not expected_alerts:
        errors.append("repository_alert_inventory_empty")

    rules = load(rules_path)
    live_alerts: set[str] = set()
    for group in (rules.get("data") or {}).get("groups") or []:
        for rule in group.get("rules") or []:
            if rule.get("type") != "alerting":
                continue
            name = rule.get("name")
            if not isinstance(name, str) or not name:
                errors.append("live_alert_name_invalid")
                continue
            if name in live_alerts:
                errors.append("live_alert_duplicate")
            live_alerts.add(name)
            if rule.get("health") != "ok":
                errors.append(f"live_alert_unhealthy:{name}")
    if live_alerts != expected_alerts:
        errors.append("live_alert_inventory_mismatch")

    targets = load(targets_path)
    by_job: dict[str, list[dict]] = {}
    for target in (targets.get("data") or {}).get("activeTargets") or []:
        labels = target.get("labels") or {}
        job = labels.get("job")
        if isinstance(job, str):
            by_job.setdefault(job, []).append(target)
    if set(by_job) != EXPECTED_JOBS:
        errors.append("live_target_job_inventory_mismatch")
    for job in sorted(EXPECTED_JOBS):
        entries = by_job.get(job, [])
        if len(entries) != 1:
            errors.append(f"live_target_cardinality:{job}")
            continue
        if entries[0].get("health") != "up":
            errors.append(f"live_target_unhealthy:{job}")
    probe = by_job.get("blackbox-https", [])
    if len(probe) == 1 and (probe[0].get("labels") or {}).get("instance") != expected_probe:
        errors.append("live_blackbox_target_mismatch")

    series = load(series_path).get("data") or []
    by_metric: dict[str, list[dict]] = {}
    for labels in series:
        if isinstance(labels, dict) and isinstance(labels.get("__name__"), str):
            by_metric.setdefault(labels["__name__"], []).append(labels)
    required_labels = {
        "saydin_activity_log_write_failures_total": {"job", "outcome"},
        "saydin_activity_log_queue_drops_total": {"job", "action"},
        "saydin_activity_log_queue_rejected_writes_total": {"job", "action", "reason"},
        "saydin_process_start_time_seconds": {"job"},
        "http_server_request_duration_seconds_count": {"job", "http_response_status_code"},
    }
    if require_ingestion:
        required_labels["saydin_market_calendar_coverage_horizon_days"] = {"job", "calendar"}
    for metric, labels in required_labels.items():
        candidates = by_metric.get(metric, [])
        if not candidates:
            errors.append(f"live_metric_missing:{metric}")
        elif not any(labels.issubset(candidate) for candidate in candidates):
            errors.append(f"live_metric_labels_invalid:{metric}")
    return sorted(set(errors))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rule-root", type=Path, required=True)
    parser.add_argument("--rules-response", type=Path, required=True)
    parser.add_argument("--targets-response", type=Path, required=True)
    parser.add_argument("--series-response", type=Path, required=True)
    parser.add_argument("--expected-probe", required=True)
    parser.add_argument("--require-ingestion", action="store_true")
    args = parser.parse_args()
    try:
        errors = validate(args.rule_root, args.rules_response, args.targets_response,
                          args.series_response, args.expected_probe,
                          args.require_ingestion)
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError):
        errors = ["runtime_response_invalid"]
    if errors:
        for error in errors:
            print(f"prometheus_runtime_rejected:{error}", file=sys.stderr)
        return 78
    print("prometheus_runtime_accepted")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
