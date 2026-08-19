#!/usr/bin/env python3
"""Bind an operator-owned non-secret environment to one verified release manifest."""

import argparse
import json
import re
import sys
from pathlib import Path

KEY = re.compile(r"^[A-Z][A-Z0-9_]*$")
IMAGE_KEYS = {
    "api": "SAYDIN_API_IMAGE",
    "ingestion": "SAYDIN_INGESTION_IMAGE",
    "control": "SAYDIN_CONTROL_IMAGE",
    "calendar": "SAYDIN_CALENDAR_IMAGE",
    "dqa": "SAYDIN_DQA_IMAGE",
    "backup": "SAYDIN_BACKUP_IMAGE",
    "caddy": "SAYDIN_CADDY_IMAGE",
}
RUNTIME_IMAGE_KEYS = {
    "timescale": "SAYDIN_TIMESCALE_IMAGE", "redis": "SAYDIN_REDIS_IMAGE",
    "postgresExporter": "SAYDIN_POSTGRES_EXPORTER_IMAGE", "redisExporter": "SAYDIN_REDIS_EXPORTER_IMAGE",
    "otel": "SAYDIN_OTEL_IMAGE", "prometheus": "SAYDIN_PROMETHEUS_IMAGE",
    "alertmanager": "SAYDIN_ALERTMANAGER_IMAGE", "blackbox": "SAYDIN_BLACKBOX_IMAGE",
    "nodeExporter": "SAYDIN_NODE_EXPORTER_IMAGE", "tempo": "SAYDIN_TEMPO_IMAGE",
    "loki": "SAYDIN_LOKI_IMAGE",
}
FORBIDDEN = re.compile(r"(?i)(password|secret|token|api[_-]?key|app[_-]?id|connectionstrings)")
ALLOWED_PATH_KEY = re.compile(r"(?i)(?:_file|_directory|_volume|key_id)$")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        values: dict[str, str] = {}
        for number, raw in enumerate(args.base.read_text(encoding="utf-8").splitlines(), 1):
            if not raw or raw.startswith("#"):
                continue
            key, separator, value = raw.partition("=")
            if not separator or not KEY.fullmatch(key) or key in values or "\n" in value or "\r" in value:
                raise ValueError(f"invalid_env_line:{number}")
            if FORBIDDEN.search(key) and not ALLOWED_PATH_KEY.search(key):
                raise ValueError(f"raw_secret_key_forbidden:{key}")
            values[key] = value
        for image in manifest["images"]:
            values[IMAGE_KEYS[image["name"]]] = image["reference"] + "@" + image["digest"]
        for name, reference in manifest["runtimeImages"].items():
            values[RUNTIME_IMAGE_KEYS[name]] = reference
        values["SAYDIN_RELEASE_VERSION"] = manifest["releaseId"]
        values["SAYDIN_SERVICE_VERSION"] = manifest["releaseId"]
        values["SAYDIN_GIT_SHA"] = manifest["source"]["commitSha"]
        args.output.write_text("".join(f"{key}={values[key]}\n" for key in sorted(values)), encoding="utf-8")
        args.output.chmod(0o600)
        return 0
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        print(f"deployment_env_rejected:{exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
