#!/usr/bin/env python3
"""Fail-closed local Markdown link and canonical documentation validator."""

from __future__ import annotations

import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parents[2]
SKIP_PARTS = {".git", "bin", "obj", "node_modules", ".nuget", ".dotnet"}
REQUIRED_CANONICAL = (
    "README.md",
    "SECURITY.md",
    "CONTRIBUTING.md",
    "docs/architecture.md",
    "docs/development-guide.md",
    "docs/analysis/README.md",
    "docs/analysis/06-remediation-progress.md",
    "docs/decisions/README.md",
    "docs/runbooks/README.md",
)

INLINE_LINK = re.compile(r"!?\[[^\]]*\]\((?P<target><[^>]+>|[^\s)]+)")
REFERENCE_LINK = re.compile(r"^\s*\[[^\]]+\]:\s*(?P<target><[^>]+>|\S+)", re.MULTILINE)
URI_SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")
DEV_COMPOSE_COMMAND = re.compile(r"^docker compose[ \t]+.*$", re.MULTILINE)


def markdown_files() -> list[Path]:
    return sorted(
        path
        for path in ROOT.rglob("*.md")
        if path.is_file() and not any(part in SKIP_PARTS for part in path.relative_to(ROOT).parts)
    )


def normalize_target(raw: str) -> str | None:
    target = raw.strip()
    if target.startswith("<") and target.endswith(">"):
        target = target[1:-1].strip()
    if not target or target.startswith("#") or URI_SCHEME.match(target):
        return None
    parsed = urlsplit(target)
    if parsed.scheme or parsed.netloc:
        return None
    path = unquote(parsed.path)
    if not path or any(marker in path for marker in ("${", "{{", "<token>")):
        return None
    return path


def resolve(source: Path, target: str) -> Path:
    if target.startswith("/"):
        return ROOT / target.lstrip("/")
    return source.parent / target


def main() -> int:
    errors: list[str] = []
    checked = 0

    for required in REQUIRED_CANONICAL:
        if not (ROOT / required).is_file():
            errors.append(f"canonical_missing:{required}")

    files = markdown_files()
    if not files:
        errors.append("markdown_inventory_empty")

    for source in files:
        try:
            body = source.read_text(encoding="utf-8")
        except (OSError, UnicodeError) as error:
            errors.append(f"markdown_unreadable:{source.relative_to(ROOT)}:{type(error).__name__}")
            continue

        matches = list(INLINE_LINK.finditer(body)) + list(REFERENCE_LINK.finditer(body))
        for match in matches:
            raw = match.group("target")
            target = normalize_target(raw)
            if target is None:
                continue
            checked += 1
            resolved = resolve(source, target).resolve(strict=False)
            try:
                resolved.relative_to(ROOT)
            except ValueError:
                errors.append(
                    f"link_outside_repo:{source.relative_to(ROOT)}:{match.start()}:{raw}"
                )
                continue
            if not resolved.exists():
                errors.append(
                    f"link_missing:{source.relative_to(ROOT)}:{match.start()}:{raw}"
                )

        if source.relative_to(ROOT).as_posix() in {"CLAUDE.md", "CONTRIBUTING.md"}:
            for command in DEV_COMPOSE_COMMAND.findall(body):
                if ("--env-file .env" not in command
                        or "--env-file .env.database-runtime" not in command):
                    errors.append(
                        f"development_compose_env_files_missing:"
                        f"{source.relative_to(ROOT)}:{command}"
                    )

    for error in sorted(set(errors)):
        print(error, file=sys.stderr)
    if errors:
        print(f"documentation_link_validation_failed:files={len(files)}:links={checked}", file=sys.stderr)
        return 1

    print(f"documentation_link_validation_passed:files={len(files)}:local_links={checked}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
