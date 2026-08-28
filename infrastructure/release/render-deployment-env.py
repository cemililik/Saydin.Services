#!/usr/bin/env python3
"""Bind an operator-owned non-secret environment to one verified release manifest."""

import argparse
import json
import re
import sys
from pathlib import Path

from release_manifest import EXPECTED_IMAGES, RUNTIME_IMAGE_ENV_KEYS

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
FORBIDDEN = re.compile(r"(?i)(password|secret|token|api[_-]?key|app[_-]?id|connectionstrings)")
ALLOWED_PATH_KEY = re.compile(r"(?i)(?:_file|_directory|_volume|key_id)$")
EXPECTED_DEPLOYMENT_IMAGE_KEYS = set(IMAGE_KEYS.values()) | set(RUNTIME_IMAGE_ENV_KEYS.values())


def read_environment(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not raw or raw.startswith("#"):
            continue
        key, separator, value = raw.partition("=")
        if not separator or not KEY.fullmatch(key) or key in values or "\n" in value or "\r" in value:
            raise ValueError(f"invalid_env_line:{number}")
        if FORBIDDEN.search(key) and not ALLOWED_PATH_KEY.search(key):
            raise ValueError(f"raw_secret_key_forbidden:{key}")
        values[key] = value
    return values


def expected_image_bindings(manifest: object) -> dict[str, str]:
    if not isinstance(manifest, dict):
        raise ValueError("deployment_manifest_invalid")
    images = manifest.get("images")
    runtime_images = manifest.get("runtimeImages")
    if not isinstance(images, list) or not isinstance(runtime_images, dict):
        raise ValueError("deployment_manifest_image_set_invalid")
    names = [item.get("name") for item in images if isinstance(item, dict)]
    if len(names) != len(images) or set(names) != set(EXPECTED_IMAGES):
        raise ValueError("deployment_manifest_image_set_invalid")
    if set(runtime_images) != set(RUNTIME_IMAGE_ENV_KEYS):
        raise ValueError("deployment_manifest_runtime_image_set_invalid")

    expected = {
        IMAGE_KEYS[item["name"]]: item["reference"] + "@" + item["digest"]
        for item in images if item["name"] in IMAGE_KEYS
    }
    data_repair = next(item for item in images if item["name"] == "data_repair")
    if runtime_images["data_repair"] != data_repair["reference"] + "@" + data_repair["digest"]:
        raise ValueError("deployment_manifest_data_repair_image_mismatch")
    expected.update({RUNTIME_IMAGE_ENV_KEYS[name]: reference
                     for name, reference in runtime_images.items()})
    return expected


def expected_source_bindings(manifest: dict[str, object]) -> dict[str, str]:
    source = manifest.get("source")
    if not isinstance(source, dict):
        raise ValueError("deployment_manifest_source_invalid")
    release_id = manifest.get("releaseId")
    commit_sha = source.get("commitSha")
    if not isinstance(release_id, str) or not isinstance(commit_sha, str):
        raise ValueError("deployment_manifest_source_invalid")
    return {
        "SAYDIN_GIT_SHA": commit_sha,
        "SAYDIN_RELEASE_VERSION": release_id,
        "SAYDIN_SERVICE_VERSION": release_id,
    }


def validate_image_key_set(values: dict[str, str]) -> None:
    actual = {key for key in values if key.startswith("SAYDIN_") and key.endswith("_IMAGE")}
    if actual != EXPECTED_DEPLOYMENT_IMAGE_KEYS:
        raise ValueError("deployment_manifest_image_key_set_mismatch")


def verify_existing_binding(manifest: dict[str, object], values: dict[str, str]) -> None:
    validate_image_key_set(values)
    expected = expected_image_bindings(manifest)
    if any(values.get(key) != value for key, value in expected.items()):
        raise ValueError("deployment_manifest_image_mismatch")
    expected_source = expected_source_bindings(manifest)
    if any(values.get(key) != value for key, value in expected_source.items()):
        raise ValueError("deployment_manifest_source_mismatch")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--verify-existing", type=Path)
    args = parser.parse_args()
    try:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        if args.verify_existing is not None:
            if args.base is not None or args.output is not None:
                raise ValueError("deployment_env_mode_invalid")
            verify_existing_binding(manifest, read_environment(args.verify_existing))
            return 0
        if args.base is None or args.output is None:
            raise ValueError("deployment_env_render_inputs_required")

        values = read_environment(args.base)
        values.update(expected_image_bindings(manifest))
        values.update(expected_source_bindings(manifest))
        validate_image_key_set(values)
        args.output.write_text("".join(f"{key}={values[key]}\n" for key in sorted(values)), encoding="utf-8")
        args.output.chmod(0o600)
        return 0
    except (OSError, KeyError, TypeError, ValueError) as exc:
        print(f"deployment_env_rejected:{exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
