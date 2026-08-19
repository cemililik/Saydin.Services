#!/usr/bin/env python3
"""Fail-closed static contract for bounded alerts and durable telemetry backends."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


PREFIX = "https://github.com/cemililik/Saydin.Services/blob/main/docs/runbooks/"


def validate(root: Path) -> list[str]:
    errors: list[str] = []
    rule_root = root / "infrastructure" / "prometheus" / "rules"
    runbook_root = root / "docs" / "runbooks"
    alerts = 0
    rule_files = sorted(rule_root.glob("*.yml"))
    if not rule_files:
        errors.append("rules_missing")
    for path in rule_files:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            errors.append("rule_unreadable")
            continue
        alert_count = len(re.findall(r"^\s*- alert:\s+\S+", text, re.M))
        alerts += alert_count
        if re.search(r"^\s*labels:\s*\{[^}]*(?:device|user|installation|ip|symbol|scenario|trace|exception)", text, re.M | re.I):
            errors.append("unbounded_alert_label")
        if len(re.findall(r"^\s*runbook_url:\s+", text, re.M)) != alert_count:
            errors.append("runbook_count")
        for value in re.findall(r"^\s*runbook_url:\s+(\S+)\s*$", text, re.M):
            if not value.startswith(PREFIX):
                errors.append("runbook_origin")
                continue
            relative = value.removeprefix(PREFIX)
            target = runbook_root / relative
            if not target.is_file() or target.resolve().parent != runbook_root.resolve():
                errors.append("runbook_missing")
    if alerts == 0:
        errors.append("no_alerts")

    otel_root = root / "infrastructure" / "otel"
    required = {
        "otel": otel_root / "otel-collector.production.yml",
        "tempo": otel_root / "tempo.production.yml",
        "loki": otel_root / "loki.production.yml",
    }
    texts: dict[str, str] = {}
    for name, path in required.items():
        try:
            text = path.read_text(encoding="utf-8")
            if not text.strip():
                raise ValueError
            texts[name] = text
        except (OSError, UnicodeError, ValueError):
            errors.append(f"telemetry_artifact_invalid:{name}")

    otel = texts.get("otel", "")
    if re.search(r"^\s*nop(?:/\S+)?:", otel, re.M) or re.search(r"exporters:\s*\[\s*nop", otel):
        errors.append("nop_exporter_forbidden")
    for token in (
        "otlp/tempo:", "endpoint: tempo:4317", "otlphttp/loki:",
        "endpoint: http://loki:3100/otlp", "storage: file_storage",
        "deployment.environment.name", "service.version", "service.instance.id",
        "vcs.ref.head.revision",
    ):
        if token not in otel:
            errors.append(f"otel_contract_missing:{token.rstrip(':')}")
    if (otel.count("sending_queue:") != 2 or otel.count("retry_on_failure:") != 2
            or otel.count("storage: file_storage") != 2):
        errors.append("otel_retry_queue_contract")
    if not re.search(r"traces:\s.*?exporters:\s*\[otlp/tempo\]", otel, re.S):
        errors.append("trace_pipeline_missing")
    if not re.search(r"logs:\s.*?exporters:\s*\[otlphttp/loki\]", otel, re.S):
        errors.append("log_pipeline_missing")

    tempo = texts.get("tempo", "")
    for token in ("backend: local", "path: /var/tempo/wal", "block_retention: ${SAYDIN_TEMPO_RETENTION}", "reporting_enabled: false"):
        if token not in tempo:
            errors.append(f"tempo_contract_missing:{token}")
    loki = texts.get("loki", "")
    for token in ("store: tsdb", "schema: v13", "retention_enabled: true", "retention_period: ${SAYDIN_LOKI_RETENTION}", "reporting_enabled: false"):
        if token not in loki:
            errors.append(f"loki_contract_missing:{token}")

    prometheus_path = root / "infrastructure" / "prometheus" / "prometheus.production.yml"
    try:
        prometheus = prometheus_path.read_text(encoding="utf-8")
        if not prometheus.strip():
            raise ValueError
    except (OSError, UnicodeError, ValueError):
        errors.append("telemetry_artifact_invalid:prometheus")
    else:
        api_job = re.search(
            r"^\s*- job_name:\s*saydin-api\s*$.*?(?=^\s*- job_name:|\Z)",
            prometheus,
            re.M | re.S,
        )
        if api_job is None or "targets: [saydin-api:9090]" not in api_job.group(0):
            errors.append("api_management_scrape_missing")
        if api_job is not None and "saydin-api:8080" in api_job.group(0):
            errors.append("api_public_scrape_forbidden")

    return sorted(set(errors))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    errors = validate(args.root.resolve())
    if errors:
        for error in errors:
            print(f"observability_validation_failed:{error}", file=sys.stderr)
        return 2
    print("observability_validation_passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
