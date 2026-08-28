#!/usr/bin/env python3
from __future__ import annotations

import copy
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
TOOL = ROOT / "infrastructure/release/release_manifest.py"
ENV_TOOL = ROOT / "infrastructure/release/render-deployment-env.py"
NAMES = ("api", "backup", "caddy", "calendar", "control", "data_repair", "dqa", "ingestion")
RUNTIME_NAMES = ("alertmanager", "blackbox", "data_repair", "loki", "nodeExporter", "otel",
                 "postgresExporter", "prometheus", "redis", "redisExporter", "tempo", "timescale")
EXTERNAL_RUNTIME_NAMES = tuple(name for name in RUNTIME_NAMES if name != "data_repair")
RUNTIME_ENV_KEYS = {
    "alertmanager": "SAYDIN_ALERTMANAGER_IMAGE", "blackbox": "SAYDIN_BLACKBOX_IMAGE",
    "data_repair": "SAYDIN_DATA_REPAIR_IMAGE",
    "loki": "SAYDIN_LOKI_IMAGE", "nodeExporter": "SAYDIN_NODE_EXPORTER_IMAGE",
    "otel": "SAYDIN_OTEL_IMAGE", "postgresExporter": "SAYDIN_POSTGRES_EXPORTER_IMAGE",
    "prometheus": "SAYDIN_PROMETHEUS_IMAGE", "redis": "SAYDIN_REDIS_IMAGE",
    "redisExporter": "SAYDIN_REDIS_EXPORTER_IMAGE", "tempo": "SAYDIN_TEMPO_IMAGE",
    "timescale": "SAYDIN_TIMESCALE_IMAGE",
}
FIRST_PARTY_ENV_KEYS = {
    "api": "SAYDIN_API_IMAGE", "backup": "SAYDIN_BACKUP_IMAGE",
    "caddy": "SAYDIN_CADDY_IMAGE", "calendar": "SAYDIN_CALENDAR_IMAGE",
    "control": "SAYDIN_CONTROL_IMAGE", "dqa": "SAYDIN_DQA_IMAGE",
    "ingestion": "SAYDIN_INGESTION_IMAGE",
}


def image(name: str, index: int) -> dict[str, object]:
    hexadecimal = f"{index + 1:064x}"
    return {"name": name, "sourceCommit": "c" * 40,
            "reference": f"ghcr.io/saydin/{name}", "digest": f"sha256:{hexadecimal}",
            "platforms": ["linux/amd64", "linux/arm64"],
            "platformDigests": {"linux/amd64": f"sha256:{index + 101:064x}",
                                "linux/arm64": f"sha256:{index + 201:064x}"},
            "sbom": {"linux/amd64": {"spdxSha256": "a" * 64, "cycloneDxSha256": "b" * 64},
                     "linux/arm64": {"spdxSha256": "c" * 64, "cycloneDxSha256": "d" * 64}}}


def manifest(previous: str | None = None) -> dict[str, object]:
    images = [image(name, index) for index, name in enumerate(NAMES)]
    repair_image = next(item for item in images if item["name"] == "data_repair")
    runtime_images = {name: f"registry.invalid/vendor/{name.lower()}@sha256:{index + 301:064x}"
                      for index, name in enumerate(EXTERNAL_RUNTIME_NAMES)}
    runtime_images["data_repair"] = repair_image["reference"] + "@" + repair_image["digest"]
    return {"schemaVersion": 1, "releaseId": "v1.2.3",
            "source": {"repository": "saydin/services", "commitSha": "c" * 40,
                       "workflowRef": "saydin/services/.github/workflows/release-images.yml@refs/heads/main"},
            "database": {"terminalMigration": "022_release_contract", "trustRootSha256": "d" * 64},
            "compatibility": {"minimumMigration": "021_api_trust_expand", "maximumMigration": "022_release_contract",
                              "previousManifestSha256": previous},
            "images": images,
            "runtimeImages": runtime_images,
            "backupPolicy": {"rpoMinutes": 15, "rtoMinutes": 120, "walDays": 14,
                             "weeklyWeeks": 8, "monthlyMonths": 12}}


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")


def run_tool(tool: Path, *args: str, expected: int) -> subprocess.CompletedProcess[str]:
    result = subprocess.run([sys.executable, str(tool), *args], capture_output=True, text=True)
    if result.returncode != expected:
        raise AssertionError((tool.name, args, result.returncode, result.stdout, result.stderr))
    return result


def run(*args: str, expected: int) -> None:
    run_tool(TOOL, *args, expected=expected)


def env_values(path: Path) -> dict[str, str]:
    return dict(line.split("=", 1) for line in path.read_text(encoding="utf-8").splitlines())


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="saydin-release-test-") as raw:
        temp = Path(raw)
        valid = temp / "valid.json"
        write(valid, manifest())
        run("verify", "--manifest", str(valid), expected=0)
        records = temp / "records"; records.mkdir()
        value = manifest()
        for record in value["images"]:  # type: ignore[index]
            write(records / f"{record['name']}.json", record)
        runtime = temp / "runtime.json"
        write(runtime, {name: value["runtimeImages"][name]  # type: ignore[index]
                        for name in EXTERNAL_RUNTIME_NAMES})
        created = temp / "created.json"
        run("create", "--records", str(records), "--runtime-images", str(runtime),
            "--release-id", "v1.2.3", "--repository", "saydin/services", "--commit-sha", "c" * 40,
            "--workflow-ref", "saydin/services/.github/workflows/release-images.yml@refs/heads/main",
            "--terminal-migration", "022_release_contract", "--trust-root-sha256", "d" * 64,
            "--minimum-migration", "021_api_trust_expand", "--maximum-migration", "022_release_contract",
            "--previous-manifest-sha256", "none", "--output", str(created), expected=0)
        run("verify", "--manifest", str(created), expected=0)
        runtime_with_derived = temp / "runtime-with-derived.json"
        write(runtime_with_derived, value["runtimeImages"])
        run("create", "--records", str(records), "--runtime-images", str(runtime_with_derived),
            "--release-id", "v1.2.3", "--repository", "saydin/services", "--commit-sha", "c" * 40,
            "--workflow-ref", "saydin/services/.github/workflows/release-images.yml@refs/heads/main",
            "--terminal-migration", "022_release_contract", "--trust-root-sha256", "d" * 64,
            "--minimum-migration", "021_api_trust_expand", "--maximum-migration", "022_release_contract",
            "--previous-manifest-sha256", "none", "--output", str(temp / "bad-created.json"), expected=2)
        runtime_missing_external = temp / "runtime-missing-external.json"
        write(runtime_missing_external, {
            name: value["runtimeImages"][name] for name in EXTERNAL_RUNTIME_NAMES  # type: ignore[index]
            if name != "loki"
        })
        run("create", "--records", str(records), "--runtime-images", str(runtime_missing_external),
            "--release-id", "v1.2.3", "--repository", "saydin/services", "--commit-sha", "c" * 40,
            "--workflow-ref", "saydin/services/.github/workflows/release-images.yml@refs/heads/main",
            "--terminal-migration", "022_release_contract", "--trust-root-sha256", "d" * 64,
            "--minimum-migration", "021_api_trust_expand", "--maximum-migration", "022_release_contract",
            "--previous-manifest-sha256", "none", "--output", str(temp / "missing-created.json"), expected=2)
        mutations = []
        missing = manifest(); missing["images"] = missing["images"][:-1]  # type: ignore[index]
        mutations.append(missing)
        mutable = manifest(); mutable["images"][0]["digest"] = "latest"  # type: ignore[index]
        mutations.append(mutable)
        placeholder = manifest(); placeholder["releaseId"] = "CHANGE_ME"
        mutations.append(placeholder)
        policy = manifest(); policy["backupPolicy"]["rpoMinutes"] = 60  # type: ignore[index]
        mutations.append(policy)
        range_drift = manifest(); range_drift["compatibility"]["maximumMigration"] = "021_api_trust_expand"  # type: ignore[index]
        mutations.append(range_drift)
        source_drift = manifest(); source_drift["images"][0]["sourceCommit"] = "e" * 40  # type: ignore[index]
        mutations.append(source_drift)
        runtime_missing = manifest(); del runtime_missing["runtimeImages"]["loki"]  # type: ignore[index]
        mutations.append(runtime_missing)
        repair_runtime_missing = manifest(); del repair_runtime_missing["runtimeImages"]["data_repair"]  # type: ignore[index]
        mutations.append(repair_runtime_missing)
        repair_runtime_drift = manifest()
        repair_runtime_drift["runtimeImages"]["data_repair"] = (  # type: ignore[index]
            "ghcr.io/saydin/data_repair@sha256:" + "e" * 64)
        mutations.append(repair_runtime_drift)
        runtime_extra = manifest(); runtime_extra["runtimeImages"]["unexpected"] = (  # type: ignore[index]
            "registry.invalid/vendor/unexpected@sha256:" + "e" * 64)
        mutations.append(runtime_extra)
        for index, value in enumerate(mutations):
            path = temp / f"bad-{index}.json"; write(path, value)
            run("verify", "--manifest", str(path), expected=2)
        duplicate = temp / "duplicate.json"
        duplicate.write_text('{"schemaVersion":1,"schemaVersion":1}\n', encoding="utf-8")
        run("verify", "--manifest", str(duplicate), expected=2)

        target = temp / "target.json"; write(target, manifest())
        target_digest = hashlib.sha256(target.read_bytes()).hexdigest()
        current_value = manifest(target_digest); current_value["releaseId"] = "v1.2.4"
        current = temp / "current.json"; write(current, current_value)
        run("verify-rollback", "--current", str(current), "--target", str(target), expected=0)
        wrong = copy.deepcopy(current_value); wrong["compatibility"]["previousManifestSha256"] = "e" * 64  # type: ignore[index]
        wrong_path = temp / "wrong.json"; write(wrong_path, wrong)
        run("verify-rollback", "--current", str(wrong_path), "--target", str(target), expected=2)

        base = temp / "base.env"
        all_image_keys = set(FIRST_PARTY_ENV_KEYS.values()) | set(RUNTIME_ENV_KEYS.values())
        base.write_text(
            "SAYDIN_DATABASE=saydin\n"
            + "".join(f"{key}=stale.invalid/image@sha256:{'f' * 64}\n"
                      for key in sorted(all_image_keys)),
            encoding="utf-8")
        rendered = temp / "rendered.env"
        run_tool(ENV_TOOL, "--base", str(base), "--manifest", str(valid),
                 "--output", str(rendered), expected=0)
        rendered_values = env_values(rendered)
        rendered_image_keys = {key for key in rendered_values if key.endswith("_IMAGE")}
        if rendered_image_keys != all_image_keys:
            raise AssertionError(("rendered_image_key_set", rendered_image_keys))
        for name, env_key in RUNTIME_ENV_KEYS.items():
            if rendered_values[env_key] != manifest()["runtimeImages"][name]:  # type: ignore[index]
                raise AssertionError(("runtime_binding_mismatch", name, env_key))
        run_tool(ENV_TOOL, "--manifest", str(valid), "--verify-existing", str(rendered), expected=0)

        missing_env = temp / "missing.env"
        missing_env.write_text("".join(
            line for line in rendered.read_text(encoding="utf-8").splitlines(keepends=True)
            if not line.startswith("SAYDIN_LOKI_IMAGE=")), encoding="utf-8")
        run_tool(ENV_TOOL, "--manifest", str(valid), "--verify-existing", str(missing_env), expected=2)

        extra_env = temp / "extra.env"
        extra_env.write_text(
            rendered.read_text(encoding="utf-8")
            + f"SAYDIN_UNEXPECTED_IMAGE=registry.invalid/unexpected@sha256:{'e' * 64}\n",
            encoding="utf-8")
        run_tool(ENV_TOOL, "--manifest", str(valid), "--verify-existing", str(extra_env), expected=2)

        mismatch_env = temp / "mismatch.env"
        mismatch_env.write_text(
            rendered.read_text(encoding="utf-8").replace(
                rendered_values["SAYDIN_TEMPO_IMAGE"],
                "registry.invalid/vendor/tempo@sha256:" + "a" * 64),
            encoding="utf-8")
        run_tool(ENV_TOOL, "--manifest", str(valid), "--verify-existing", str(mismatch_env), expected=2)

        repair_mismatch_env = temp / "repair-mismatch.env"
        repair_mismatch_env.write_text(
            rendered.read_text(encoding="utf-8").replace(
                rendered_values["SAYDIN_DATA_REPAIR_IMAGE"],
                "ghcr.io/saydin/data_repair@sha256:" + "f" * 64),
            encoding="utf-8")
        run_tool(ENV_TOOL, "--manifest", str(valid), "--verify-existing", str(repair_mismatch_env), expected=2)
    print("release_manifest_self_test_passed:25")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
