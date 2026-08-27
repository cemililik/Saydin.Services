#!/usr/bin/env python3
"""Mutation tests for live Prometheus inventory/target admission."""

from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
from pathlib import Path


# Deliberately non-TLS, non-resolvable probe target. The mutation below proves the
# validator rejects an unreviewed blackbox instance; making it https would remove the
# very property under test.
UNREVIEWED_PROBE_TARGET = "http://metadata.invalid/"  # NOSONAR (python:S5332)


def load_validator(path: Path):
    spec = importlib.util.spec_from_file_location("prometheus_runtime_validator", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("validator_load_failed")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    validator = load_validator(Path(__file__).with_name("validate-prometheus-runtime.py"))
    names: list[str] = []
    import re
    for path in sorted((root / "infrastructure/prometheus/rules").glob("*.yml")):
        names.extend(re.findall(r"^\s*- alert:\s+(\S+)", path.read_text(encoding="utf-8"), re.M))
    rules = {"status": "success", "data": {"groups": [{"rules": [
        {"type": "alerting", "name": name, "health": "ok"} for name in names
    ]}]}}
    targets = {"status": "success", "data": {"activeTargets": [
        {
            "labels": {
                "job": job,
                **({"instance": "https://api.validation.test/health/live"}
                   if job == "blackbox-https" else {}),
            },
            "health": "up",
        }
        for job in sorted(validator.EXPECTED_JOBS)
    ]}}
    series = {"status": "success", "data": [
        {"__name__": "saydin_activity_log_write_failures_total", "job": "saydin-api", "outcome": "retry_exhausted"},
        {"__name__": "saydin_activity_log_queue_drops_total", "job": "saydin-api", "action": "other"},
        {"__name__": "saydin_activity_log_queue_rejected_writes_total", "job": "saydin-api", "action": "other", "reason": "writer_completed"},
        {"__name__": "saydin_process_start_time_seconds", "job": "saydin-api"},
        {"__name__": "http_server_request_duration_seconds_count", "job": "saydin-api", "http_response_status_code": "200"},
    ]}
    with tempfile.TemporaryDirectory(prefix="saydin-prometheus-runtime-") as directory:
        workspace = Path(directory)
        rule_path = workspace / "rules.json"
        target_path = workspace / "targets.json"
        series_path = workspace / "series.json"

        def validate(rule_value: dict, target_value: dict,
                     series_value: dict | None = None,
                     require_ingestion: bool = False) -> list[str]:
            resolved_series = series if series_value is None else series_value
            rule_path.write_text(json.dumps(rule_value), encoding="utf-8")
            target_path.write_text(json.dumps(target_value), encoding="utf-8")
            series_path.write_text(json.dumps(resolved_series), encoding="utf-8")
            return validator.validate(
                root / "infrastructure/prometheus/rules", rule_path, target_path, series_path,
                "https://api.validation.test/health/live", require_ingestion)

        if validate(rules, targets):
            raise SystemExit("monitoring_runtime_self_test_failed:baseline")
        mutations = {
            "missing_rule": lambda r, _t: r["data"]["groups"][0]["rules"].pop(),
            "unhealthy_rule": lambda r, _t: r["data"]["groups"][0]["rules"][0].__setitem__("health", "err"),
            "duplicate_rule": lambda r, _t: r["data"]["groups"][0]["rules"].append(
                copy.deepcopy(r["data"]["groups"][0]["rules"][0])),
            "missing_job": lambda _r, t: t["data"]["activeTargets"].pop(),
            "unhealthy_target": lambda _r, t: t["data"]["activeTargets"][0].__setitem__("health", "down"),
            "duplicate_target": lambda _r, t: t["data"]["activeTargets"].append(
                copy.deepcopy(t["data"]["activeTargets"][0])),
            "extra_job": lambda _r, t: t["data"]["activeTargets"].append(
                {"labels": {"job": "unreviewed"}, "health": "up"}),
            "arbitrary_probe": lambda _r, t: next(
                item for item in t["data"]["activeTargets"]
                if item["labels"]["job"] == "blackbox-https")["labels"].__setitem__(
                    "instance", UNREVIEWED_PROBE_TARGET),
        }
        for name, mutate in mutations.items():
            candidate_rules = copy.deepcopy(rules)
            candidate_targets = copy.deepcopy(targets)
            mutate(candidate_rules, candidate_targets)
            if not validate(candidate_rules, candidate_targets):
                raise SystemExit(f"monitoring_runtime_self_test_failed:{name}")
        missing_metric = copy.deepcopy(series)
        missing_metric["data"].pop()
        if not validate(rules, targets, missing_metric):
            raise SystemExit("monitoring_runtime_self_test_failed:missing_metric")
        missing_label = copy.deepcopy(series)
        next(item for item in missing_label["data"]
             if item["__name__"] == "saydin_activity_log_queue_rejected_writes_total").pop("reason")
        if not validate(rules, targets, missing_label):
            raise SystemExit("monitoring_runtime_self_test_failed:missing_metric_label")

        ingestion_series = copy.deepcopy(series)
        ingestion_series["data"].append({
            "__name__": "saydin_market_calendar_coverage_horizon_days",
            "job": "otel-pipeline",
            "calendar": "tcmb_indicative_fx",
        })
        if validate(rules, targets, ingestion_series, require_ingestion=True):
            raise SystemExit("monitoring_runtime_self_test_failed:ingestion_baseline")
        if not validate(rules, targets, series, require_ingestion=True):
            raise SystemExit("monitoring_runtime_self_test_failed:ingestion_metric_missing")
        ingestion_missing_label = copy.deepcopy(ingestion_series)
        ingestion_missing_label["data"][-1].pop("calendar")
        if not validate(rules, targets, ingestion_missing_label, require_ingestion=True):
            raise SystemExit("monitoring_runtime_self_test_failed:ingestion_metric_label")
    print(f"monitoring_runtime_self_test_passed:{len(mutations) + 4}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
