#!/usr/bin/env python3
"""Build one release image record without accepting untrusted JSON fragments."""

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

NAMES = {
    "api", "ingestion", "control", "calendar", "data_repair", "dqa", "backup", "caddy",
}


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--name", required=True)
    parser.add_argument("--reference", required=True)
    parser.add_argument("--digest", required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--amd64-digest", required=True)
    parser.add_argument("--arm64-digest", required=True)
    parser.add_argument("--amd64-spdx", required=True, type=Path)
    parser.add_argument("--amd64-cyclonedx", required=True, type=Path)
    parser.add_argument("--arm64-spdx", required=True, type=Path)
    parser.add_argument("--arm64-cyclonedx", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.name not in NAMES:
        return 2
    if not re.fullmatch(r"ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+", args.reference):
        return 2
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", args.digest):
        return 2
    if not re.fullmatch(r"[0-9a-f]{40}", args.source_commit):
        return 2
    if any(not re.fullmatch(r"sha256:[0-9a-f]{64}", item) for item in (args.amd64_digest, args.arm64_digest)):
        return 2
    if len({args.digest, args.amd64_digest, args.arm64_digest}) != 3:
        return 2
    try:
        record = {
            "name": args.name,
            "sourceCommit": args.source_commit,
            "reference": args.reference,
            "digest": args.digest,
            "platforms": ["linux/amd64", "linux/arm64"],
            "platformDigests": {"linux/amd64": args.amd64_digest, "linux/arm64": args.arm64_digest},
            "sbom": {
                "linux/amd64": {"spdxSha256": digest(args.amd64_spdx), "cycloneDxSha256": digest(args.amd64_cyclonedx)},
                "linux/arm64": {"spdxSha256": digest(args.arm64_spdx), "cycloneDxSha256": digest(args.arm64_cyclonedx)},
            },
        }
        args.output.write_text(json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
        return 0
    except OSError:
        return 2


if __name__ == "__main__":
    sys.exit(main())
