#!/usr/bin/env python3
"""Fail-closed static contract for bounded alerts and durable telemetry backends."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


PREFIX = "https://github.com/cemililik/Saydin.Services/blob/main/docs/runbooks/"
SENSITIVE_LABEL = re.compile(
    r"(?:device|user|installation|ip|symbol|scenario|trace|exception)(?:_|$)", re.I)
WATCHDOG_ALERT = "SaydinWatchdog"


def rule_label_keys(text: str) -> set[str]:
    """Read inline and block-style alert labels without a YAML dependency."""
    result: set[str] = set()
    lines = text.splitlines()
    index = 0
    while index < len(lines):
        match = re.match(r"^([ \t]*)labels:[ \t]*(.*)$", lines[index])
        if match is None:
            index += 1
            continue
        indent = len(match.group(1))
        inline = match.group(2).strip()
        if inline.startswith("{") and inline.endswith("}"):
            result.update(
                item.group(1) for item in re.finditer(r"(?:^|[,{}])\s*([A-Za-z_][A-Za-z0-9_]*)\s*:", inline))
        index += 1
        while index < len(lines):
            nested = re.match(r"^(\s*)([A-Za-z_][A-Za-z0-9_]*):", lines[index])
            if nested is None:
                if lines[index].strip() and len(lines[index]) - len(lines[index].lstrip()) <= indent:
                    break
                index += 1
                continue
            if len(nested.group(1)) <= indent:
                break
            result.add(nested.group(2))
            index += 1
    return result


JOB_HEADER = re.compile(r"^[ \t]*- job_name:[ \t]*(\S+)[ \t]*$", re.M)


def prometheus_job(document: str, name: str) -> str | None:
    """Return the scrape_config block for `name`, or None when it is absent.

    Slicing between consecutive `- job_name:` headers keeps each block bounded without a
    lookahead-terminated `.*?` scan, which backtracked across the whole document.
    """
    headers = list(JOB_HEADER.finditer(document))
    for index, header in enumerate(headers):
        if header.group(1) != name:
            continue
        end = headers[index + 1].start() if index + 1 < len(headers) else len(document)
        return document[header.start():end]
    return None


def validate(root: Path) -> list[str]:
    errors: list[str] = []
    rule_root = root / "infrastructure" / "prometheus" / "rules"
    runbook_root = root / "docs" / "runbooks"
    alerts = 0
    alert_names: set[str] = set()
    rule_files = sorted(rule_root.glob("*.yml"))
    if not rule_files:
        errors.append("rules_missing")
    for path in rule_files:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            errors.append("rule_unreadable")
            continue
        names = re.findall(r"^\s*- alert:\s+(\S+)", text, re.M)
        alert_count = len(names)
        duplicate_names = alert_names.intersection(names)
        if duplicate_names:
            errors.append("alert_name_duplicate")
        alert_names.update(names)
        alerts += alert_count
        if any(SENSITIVE_LABEL.search(key) for key in rule_label_keys(text)):
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
    test_root = root / "infrastructure" / "prometheus" / "tests"
    test_files = sorted(test_root.glob("*.test.yml"))
    positive: set[str] = set()
    negative: set[str] = set()
    for path in test_files:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            errors.append("rule_test_unreadable")
            continue
        for match in re.finditer(
                r"^[ \t]*alertname:[ \t]*(\S+)[ \t]*\n[ \t]*exp_alerts:[ \t]*(\[\])?", text, re.M):
            (negative if match.group(2) else positive).add(match.group(1))
        for match in re.finditer(
                r"\{[^}\n]*alertname:\s*([A-Za-z][A-Za-z0-9]+)[^}\n]*exp_alerts:\s*\[\][^}\n]*\}",
                text):
            negative.add(match.group(1))
    if alert_names - positive:
        errors.append("alert_positive_test_inventory_mismatch")
    # A continuously firing dead-man switch has no healthy non-firing state.
    if (alert_names - {WATCHDOG_ALERT}) - negative:
        errors.append("alert_negative_test_inventory_mismatch")
    if (positive | negative) - alert_names:
        errors.append("test_unknown_alert")

    for path in sorted(runbook_root.glob("*.md")):
        try:
            references = set(re.findall(r"\b(?:Saydin|Telemetry)[A-Z][A-Za-z0-9]+", path.read_text(encoding="utf-8")))
        except OSError:
            errors.append("runbook_unreadable")
            continue
        if references - alert_names:
            errors.append("runbook_alert_reference_unknown")
    host_backup = rule_root / "host-backup.yml"
    host_backup_text = host_backup.read_text(encoding="utf-8") if host_backup.is_file() else ""
    for token in (
        "SaydinBackupLoginValidityMetricMissing",
        "SaydinBackupLoginExpiring",
        "SaydinBackupLoginExpired",
        "saydin_backup_login_valid_until_timestamp_seconds",
        "backup-login-renewal.md",
    ):
        if token not in host_backup_text:
            errors.append(f"backup_validity_alert_missing:{token}")

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
    if not re.search(r"key:\s*service\.instance\.id\s+value:\s*\$\{env:SAYDIN_DEPLOYMENT_ID\}\s+.*?action:\s*insert", otel, re.S):
        errors.append("service_instance_fallback_invalid")
    if not re.search(r"resource_to_telemetry_conversion:\s*\n\s*#?.*?enabled:\s*false", otel, re.S):
        errors.append("resource_label_conversion_enabled")
    if not re.search(
            r"health_check:\s*\n(?:\s*#.*\n)*\s*endpoint:\s*0\.0\.0\.0:13133\s*$",
            otel,
            re.M):
        errors.append("otel_network_health_endpoint_invalid")

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
        api_job = prometheus_job(prometheus, "saydin-api")
        if api_job is None or "targets: [saydin-api:9090]" not in api_job:
            errors.append("api_management_scrape_missing")
        if api_job is not None and "saydin-api:8080" in api_job:
            errors.append("api_public_scrape_forbidden")
        blackbox_job = prometheus_job(prometheus, "blackbox-https")
        if (blackbox_job is None or "file_sd_configs:" not in blackbox_job
                or "replacement: blackbox-exporter:9115" not in blackbox_job):
            errors.append("blackbox_static_target_contract_missing")

    tls_rules = (rule_root / "tls-runtime.yml").read_text(encoding="utf-8")
    for token in (
            "SaydinWatchdog", "expr: vector(1)", "severity: watchdog",
            "resets(otelcol_process_uptime", "saydin_process_start_time_seconds"):
        if token not in tls_rules:
            errors.append(f"runtime_alert_contract_missing:{token}")

    alertmanager = root / "infrastructure" / "alertmanager" / "alertmanager.template.yml"
    try:
        alertmanager_text = alertmanager.read_text(encoding="utf-8")
    except OSError:
        errors.append("alertmanager_template_unreadable")
    else:
        for token in ('severity="watchdog"', "receiver: external-watchdog",
                      "name: external-watchdog", "repeat_interval: 1m"):
            if token not in alertmanager_text:
                errors.append(f"watchdog_route_missing:{token}")

    deploy_path = root / "infrastructure" / "release" / "deploy-release.sh"
    try:
        deploy_text = deploy_path.read_text(encoding="utf-8")
    except OSError:
        errors.append("deployment_monitoring_admission_unreadable")
    else:
        for token in (
                "fetch_monitoring_runtime()", "series_start=$((series_end - 300))",
                "&start=$series_start&end=$series_end"):
            if token not in deploy_text:
                errors.append(f"deployment_metric_freshness_window_missing:{token}")

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
