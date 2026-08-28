#!/usr/bin/env python3
"""Fail closed unless a TRX contains enough all-passing, all-executed tests."""

from __future__ import annotations

import argparse
from collections import Counter
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("trx", type=Path)
    parser.add_argument("--minimum-executed", type=int, default=20)
    return parser.parse_args()


def counter_value(counters: ET.Element, name: str) -> int:
    raw = counters.get(name)
    if raw is None or not raw.isdigit():
        raise ValueError(f"TRX Counters.{name} eksik veya sayısal değil")
    return int(raw)


def main() -> int:
    args = parse_args()
    if args.minimum_executed < 1:
        raise ValueError("--minimum-executed pozitif olmalıdır")
    if not args.trx.is_file():
        raise FileNotFoundError(f"TRX bulunamadı: {args.trx}")

    root = ET.parse(args.trx).getroot()
    counters = root.find(".//{*}Counters")
    if counters is None:
        raise ValueError("TRX Counters düğümü bulunamadı")

    total = counter_value(counters, "total")
    executed = counter_value(counters, "executed")
    passed = counter_value(counters, "passed")
    prohibited = {
        name: counter_value(counters, name)
        for name in (
            "failed",
            "error",
            "timeout",
            "aborted",
            "inconclusive",
            "passedButRunAborted",
            "notExecuted",
            "notRunnable",
            "disconnected",
            "warning",
            "completed",
            "inProgress",
            "pending",
        )
    }

    result_nodes = root.findall(".//{*}UnitTestResult")
    outcomes = Counter(node.get("outcome", "<missing>") for node in result_nodes)
    errors: list[str] = []

    if total < args.minimum_executed or executed < args.minimum_executed:
        errors.append(
            f"en az {args.minimum_executed} test gerekli; total={total}, executed={executed}"
        )
    if total != executed:
        errors.append(f"tüm testler execute edilmedi: total={total}, executed={executed}")
    if passed != executed:
        errors.append(f"tüm executed testler geçmedi: passed={passed}, executed={executed}")

    non_zero = {name: value for name, value in prohibited.items() if value != 0}
    if non_zero:
        errors.append(f"yasak TRX counter değerleri: {non_zero}")

    non_passed_outcomes = {
        outcome: count for outcome, count in outcomes.items() if outcome != "Passed"
    }
    if len(result_nodes) != total:
        errors.append(f"UnitTestResult sayısı total ile eşleşmiyor: {len(result_nodes)} != {total}")
    if non_passed_outcomes:
        errors.append(f"Passed dışı test sonuçları: {non_passed_outcomes}")

    if errors:
        print("Integration TRX gate FAIL:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print(
        "Integration TRX gate PASS: "
        f"total={total}, executed={executed}, passed={passed}, failed=0, notExecuted=0"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ET.ParseError, OSError, ValueError) as exc:
        print(f"Integration TRX gate FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
