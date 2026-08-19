#!/usr/bin/env python3
"""Merge Cobertura facts and enforce weighted, namespace and diff coverage floors."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path


BRANCH_COUNTS = re.compile(r"\((\d+)\s*/\s*(\d+)\)")
DIFF_HEADER = re.compile(r"^\+\+\+ b/(.+)$")
DIFF_RANGE = re.compile(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")


@dataclass
class LineFact:
    hits: int = 0
    branches_covered: int = 0
    branches_total: int = 0
    class_name: str = ""


def fail(code: str) -> "None":
    print(f"coverage_gate_failed:{code}", file=sys.stderr)
    raise SystemExit(2)


def normalize_path(value: str, repo: Path) -> str:
    candidate = value.replace("\\", "/")
    repo_text = str(repo.resolve()).replace("\\", "/").rstrip("/") + "/"
    if candidate.startswith(repo_text):
        candidate = candidate[len(repo_text):]
    elif candidate.startswith("/"):
        # Coverage is collected in a pinned SDK container mounted at /repo,
        # while this verifier runs on the host checkout. Resolve only a suffix
        # that is an actual file in this exact checkout; never trust or merely
        # strip an arbitrary absolute prefix.
        parts = [part for part in candidate.split("/") if part]
        matches: list[str] = []
        for index in range(len(parts)):
            suffix = "/".join(parts[index:])
            if (repo / suffix).is_file():
                matches.append(suffix)
        if len(matches) == 1:
            candidate = matches[0]
    while candidate.startswith("./"):
        candidate = candidate[2:]
    return candidate


def parse_reports(paths: list[Path], repo: Path) -> dict[tuple[str, int], LineFact]:
    if not paths:
        fail("report_missing")
    facts: dict[tuple[str, int], LineFact] = {}
    for path in paths:
        if not path.is_file() or path.stat().st_size == 0:
            fail(f"report_missing:{path.name}")
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError):
            fail(f"report_malformed:{path.name}")
        if root.tag != "coverage":
            fail(f"report_not_cobertura:{path.name}")
        classes = root.findall("./packages/package/classes/class")
        if not classes:
            fail(f"report_empty:{path.name}")
        for klass in classes:
            filename = normalize_path(klass.attrib.get("filename", ""), repo)
            class_name = klass.attrib.get("name", "")
            if not filename or not class_name:
                fail(f"report_class_invalid:{path.name}")
            for line in klass.findall("./lines/line"):
                try:
                    number = int(line.attrib["number"])
                    hits = int(line.attrib.get("hits", "0"))
                except (KeyError, ValueError):
                    fail(f"report_line_invalid:{path.name}")
                covered = total = 0
                if line.attrib.get("branch", "false").lower() == "true":
                    match = BRANCH_COUNTS.search(line.attrib.get("condition-coverage", ""))
                    if not match:
                        fail(f"report_branch_invalid:{path.name}")
                    covered, total = map(int, match.groups())
                key = (filename, number)
                prior = facts.get(key)
                if prior is None:
                    facts[key] = LineFact(hits, covered, total, class_name)
                else:
                    prior.hits = max(prior.hits, hits)
                    prior.branches_covered = max(prior.branches_covered, covered)
                    prior.branches_total = max(prior.branches_total, total)
                    if not prior.class_name:
                        prior.class_name = class_name
    if not facts:
        fail("no_instrumented_lines")
    return facts


def rates(facts: list[LineFact]) -> tuple[float, float, int, int]:
    total_lines = len(facts)
    covered_lines = sum(1 for fact in facts if fact.hits > 0)
    total_branches = sum(fact.branches_total for fact in facts)
    covered_branches = sum(fact.branches_covered for fact in facts)
    line_rate = 100.0 * covered_lines / total_lines if total_lines else 0.0
    branch_rate = 100.0 * covered_branches / total_branches if total_branches else 100.0
    return line_rate, branch_rate, total_lines, total_branches


def changed_lines(repo: Path, base: str, head: str) -> set[tuple[str, int]]:
    revision = base if head == "WORKTREE" else f"{base}..{head}"
    try:
        output = subprocess.run(
            ["git", "diff", "--unified=0", "--no-ext-diff", revision, "--", "*.cs"],
            cwd=repo,
            check=True,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        ).stdout
    except subprocess.CalledProcessError:
        fail("changed_diff_unavailable")
    result: set[tuple[str, int]] = set()
    current: str | None = None
    for raw in output.splitlines():
        header = DIFF_HEADER.match(raw)
        if header:
            current = normalize_path(header.group(1), repo)
            continue
        match = DIFF_RANGE.match(raw)
        if match and current and current != "/dev/null":
            start = int(match.group(1))
            count = int(match.group(2) or "1")
            result.update((current, number) for number in range(start, start + count))
    if head == "WORKTREE":
        try:
            untracked = subprocess.run(
                ["git", "ls-files", "--others", "--exclude-standard", "--", "*.cs"],
                cwd=repo,
                check=True,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            ).stdout.splitlines()
            for relative in untracked:
                path = repo / relative
                line_count = len(path.read_text(encoding="utf-8").splitlines())
                result.update((normalize_path(relative, repo), number)
                              for number in range(1, line_count + 1))
        except (OSError, UnicodeError, subprocess.CalledProcessError):
            fail("changed_worktree_unavailable")
    return result


def load_policy(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        for key in ("line", "branch"):
            threshold = float(value["overall"][key])
            if not 0 <= threshold <= 100:
                raise ValueError
        changed = float(value["changed_line"])
        if not 0 <= changed <= 100:
            raise ValueError
        for limits in value["critical_namespaces"].values():
            if any(not 0 <= float(limits[key]) <= 100 for key in ("line", "branch")):
                raise ValueError
        return value
    except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError):
        fail("policy_invalid")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reports", type=Path, nargs="+", required=True)
    parser.add_argument("--policy", type=Path, required=True)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--diff-base")
    parser.add_argument("--diff-head", default="HEAD")
    args = parser.parse_args()

    repo = args.repo.resolve()
    policy = load_policy(args.policy)
    merged = parse_reports(args.reports, repo)
    errors: list[str] = []
    summary: list[str] = ["## Coverage gate", "", "| Scope | Line | Branch | Instrumented lines | Branches |", "|---|---:|---:|---:|---:|"]

    line, branch, line_count, branch_count = rates(list(merged.values()))
    summary.append(f"| weighted unique source lines | {line:.2f}% | {branch:.2f}% | {line_count} | {branch_count} |")
    if line < float(policy["overall"]["line"]):
        errors.append(f"overall_line:{line:.2f}")
    if branch < float(policy["overall"]["branch"]):
        errors.append(f"overall_branch:{branch:.2f}")

    for namespace, limits in policy["critical_namespaces"].items():
        selected = [fact for fact in merged.values() if fact.class_name.startswith(namespace)]
        if not selected:
            errors.append(f"namespace_missing:{namespace}")
            summary.append(f"| `{namespace}` | missing | missing | 0 | 0 |")
            continue
        ns_line, ns_branch, ns_lines, ns_branches = rates(selected)
        summary.append(f"| `{namespace}` | {ns_line:.2f}% | {ns_branch:.2f}% | {ns_lines} | {ns_branches} |")
        if ns_line < float(limits["line"]):
            errors.append(f"namespace_line:{namespace}:{ns_line:.2f}")
        if ns_branch < float(limits["branch"]):
            errors.append(f"namespace_branch:{namespace}:{ns_branch:.2f}")

    if args.diff_base:
        changed = changed_lines(repo, args.diff_base, args.diff_head)
        instrumented = [fact for key, fact in merged.items() if key in changed]
        if instrumented:
            changed_rate = 100.0 * sum(1 for fact in instrumented if fact.hits > 0) / len(instrumented)
            summary.extend(["", f"Changed executable lines: **{changed_rate:.2f}%** ({len(instrumented)} instrumented lines)"])
            if changed_rate < float(policy["changed_line"]):
                errors.append(f"changed_line:{changed_rate:.2f}")
        else:
            summary.extend(["", "Changed executable lines: not applicable (no changed instrumented C# lines)."])

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with Path(summary_path).open("a", encoding="utf-8") as stream:
            stream.write("\n".join(summary) + "\n")
    else:
        print("\n".join(summary))
    if errors:
        for error in sorted(errors):
            print(f"coverage_gate_failed:{error}", file=sys.stderr)
        return 2
    print(f"coverage_gate_passed:line={line:.2f}:branch={branch:.2f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
