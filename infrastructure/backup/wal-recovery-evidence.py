#!/usr/bin/env python3
"""Select and verify the latest off-host WAL observation snapshot."""

from __future__ import annotations

import datetime
import json
import os
import re
import stat
import sys
from pathlib import Path

HEX64 = re.compile(r"^[0-9a-f]{64}$")
WAL = re.compile(r"^[0-9A-F]{24}$")
LSN = re.compile(r"^([0-9A-F]+)/([0-9A-F]{1,8})$")
SIZE = re.compile(r"^([1-9][0-9]*)(kB|MB|GB)$")
MAX_INVENTORY = 8 * 1024 * 1024
MAX_FILES = 100_000


def instant(value: object) -> datetime.datetime:
    if not isinstance(value, str):
        raise ValueError("wal_snapshot_time_invalid")
    parsed = datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("wal_snapshot_time_invalid")
    return parsed.astimezone(datetime.timezone.utc)


def inventory(path: Path) -> tuple[str, datetime.datetime]:
    payload = path.read_bytes()
    if len(payload) > MAX_INVENTORY:
        raise ValueError("wal_snapshot_inventory_too_large")
    values = json.loads(payload)
    if not isinstance(values, list) or not values:
        raise ValueError("wal_snapshot_inventory_invalid")
    eligible: list[tuple[datetime.datetime, str]] = []
    seen: set[str] = set()
    for value in values:
        if not isinstance(value, dict):
            raise ValueError("wal_snapshot_entry_invalid")
        identifier = value.get("id")
        if not isinstance(identifier, str) or not HEX64.fullmatch(identifier) or identifier in seen:
            raise ValueError("wal_snapshot_id_invalid")
        seen.add(identifier)
        tags = value.get("tags")
        if not isinstance(tags, list) or "wal" not in tags or "wal-observation" not in tags:
            raise ValueError("wal_snapshot_tags_invalid")
        eligible.append((instant(value.get("time")), identifier))
    received, identifier = max(eligible)
    return identifier, received


def selection(path: Path) -> tuple[str, datetime.datetime, datetime.datetime]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or set(value) != {
        "schemaVersion", "snapshotId", "snapshotReceivedAt", "evaluatedAt"
    }:
        raise ValueError("wal_selection_invalid")
    identifier = value["snapshotId"]
    if value["schemaVersion"] != 1 or not isinstance(identifier, str) or not HEX64.fullmatch(identifier):
        raise ValueError("wal_selection_invalid")
    received = instant(value["snapshotReceivedAt"])
    evaluated = instant(value["evaluatedAt"])
    if received > evaluated or evaluated > datetime.datetime.now(datetime.timezone.utc):
        raise ValueError("wal_selection_time_invalid")
    return identifier, received, evaluated


def rfc3339(value: datetime.datetime) -> str:
    return value.astimezone(datetime.timezone.utc).replace(microsecond=0).isoformat().replace(
        "+00:00", "Z"
    )


def highwater_segments(timeline: int, lsn_text: str, size_text: str) -> tuple[str, str]:
    match = LSN.fullmatch(lsn_text)
    size_match = SIZE.fullmatch(size_text)
    if not (1 <= timeline <= 0xFFFFFFFF) or match is None or size_match is None:
        raise ValueError("wal_highwater_invalid")
    multiplier = {"kB": 1024, "MB": 1024**2, "GB": 1024**3}[size_match.group(2)]
    size = int(size_match.group(1)) * multiplier
    if size < 1024**2 or size > 1024**3 or size & (size - 1) or (1 << 32) % size:
        raise ValueError("wal_highwater_invalid")
    number = ((int(match.group(1), 16) << 32) + int(match.group(2), 16)) // size
    per_log = (1 << 32) // size
    def name(value: int) -> str:
        log, segment = divmod(value, per_log)
        return f"{timeline:08X}{log:08X}{segment:08X}"
    return name(number), name(max(0, number - 1))


def parse_observation(path: Path) -> tuple[str, int, int, str, str, str]:
    observation = json.loads(path.read_text(encoding="utf-8"))
    expected_keys = {
        "schemaVersion", "segment", "segmentSourceTimestamp",
        "observedTimestamp", "snapshotIncludesSegment",
        "serverTimeline", "serverLsn", "walSegmentSize",
        "serverWalSegment", "serverPreviousWalSegment",
    }
    if not isinstance(observation, dict) or set(observation) != expected_keys:
        raise ValueError("wal_observation_invalid")
    segment = observation["segment"]
    source_epoch = observation["segmentSourceTimestamp"]
    observed_epoch = observation["observedTimestamp"]
    if observation["schemaVersion"] != 1 or observation["snapshotIncludesSegment"] is not True:
        raise ValueError("wal_observation_unconfirmed")
    if not isinstance(segment, str) or not WAL.fullmatch(segment):
        raise ValueError("wal_observation_segment_invalid")
    if type(source_epoch) is not int or type(observed_epoch) is not int:
        raise ValueError("wal_observation_timestamp_invalid")
    if source_epoch <= 0 or source_epoch > observed_epoch:
        raise ValueError("wal_segment_source_timestamp_invalid")
    timeline = observation["serverTimeline"]
    lsn = observation["serverLsn"]
    size = observation["walSegmentSize"]
    if type(timeline) is not int or not isinstance(lsn, str) or not isinstance(size, str):
        raise ValueError("wal_highwater_invalid")
    current, previous = highwater_segments(timeline, lsn, size)
    if (observation["serverWalSegment"], observation["serverPreviousWalSegment"]) != (current, previous):
        raise ValueError("wal_highwater_segment_mismatch")
    if segment not in (current, previous):
        raise ValueError("wal_receiver_not_caught_up")
    return segment, source_epoch, observed_epoch, current, previous, lsn


def recovery_point(selection_path: Path, observation_path: Path) -> tuple[str, int, datetime.datetime, datetime.datetime, datetime.datetime, int]:
    _, received, evaluated = selection(selection_path)
    segment, source_epoch, observed_epoch, current, previous, lsn = parse_observation(observation_path)
    source = datetime.datetime.fromtimestamp(source_epoch, datetime.timezone.utc)
    observed = datetime.datetime.fromtimestamp(observed_epoch, datetime.timezone.utc)
    if observed > received or received - observed > datetime.timedelta(minutes=15):
        raise ValueError("wal_snapshot_receipt_invalid")
    guaranteed = max(source, observed - datetime.timedelta(seconds=300))
    age = int((evaluated - guaranteed).total_seconds())
    if age < 0 or age > 900:
        raise ValueError("wal_current_recovery_point_rpo_exceeded")
    return segment, source_epoch, observed, received, evaluated, age, current, previous, lsn


def evidence(selection_path: Path, expected_id: str, wal_root: Path, target_text: str) -> dict[str, object]:
    identifier, _, evaluated = selection(selection_path)
    if identifier != expected_id:
        raise ValueError("wal_snapshot_selection_mismatch")
    target = datetime.datetime.strptime(target_text, "%Y-%m-%dT%H:%M:%SZ").replace(
        tzinfo=datetime.timezone.utc
    )
    if target > evaluated:
        raise ValueError("wal_recovery_time_future")
    observations: list[Path] = []
    segments: dict[str, Path] = {}
    count = 0
    for root, directories, files in os.walk(wal_root, followlinks=False):
        for name in directories:
            child = Path(root, name)
            if child.is_symlink():
                raise ValueError("wal_restore_symlink_invalid")
        for name in files:
            count += 1
            if count > MAX_FILES:
                raise ValueError("wal_restore_inventory_too_large")
            child = Path(root, name)
            mode = child.lstat().st_mode
            if stat.S_ISLNK(mode) or not stat.S_ISREG(mode):
                raise ValueError("wal_restore_entry_invalid")
            if name == ".saydin-wal-observation":
                observations.append(child)
            elif WAL.fullmatch(name):
                if name in segments:
                    raise ValueError("wal_restore_duplicate_segment")
                segments[name] = child
            elif name.endswith((".partial", ".history")):
                continue
            else:
                raise ValueError("wal_restore_entry_invalid")
    if len(observations) != 1:
        raise ValueError("wal_observation_missing_or_duplicate")
    segment, source_epoch, observed, received, evaluated, age, current, previous, lsn = recovery_point(
        selection_path, observations[0]
    )
    if segment not in segments:
        raise ValueError("wal_observation_segment_invalid")
    actual_source = int(segments[segment].stat().st_mtime)
    if source_epoch != actual_source:
        raise ValueError("wal_segment_source_timestamp_invalid")
    guaranteed = max(
        datetime.datetime.fromtimestamp(source_epoch, datetime.timezone.utc),
        observed - datetime.timedelta(seconds=300),
    )
    return {
        "walSegment": segment,
        "walSegmentSourceAt": rfc3339(datetime.datetime.fromtimestamp(source_epoch, datetime.timezone.utc)),
        "walCoverageObservedAt": rfc3339(observed),
        "walReceiverCaughtUpAt": rfc3339(observed),
        "walServerLsn": lsn,
        "walServerHighwaterSegment": current,
        "walServerPreviousSegment": previous,
        "walSnapshotReceivedAt": rfc3339(received),
        "guaranteedRecoveryPointAt": rfc3339(guaranteed),
        "walEvidenceEvaluatedAt": rfc3339(evaluated),
        "currentRecoveryPointAgeSeconds": age,
    }


def main() -> int:
    try:
        if len(sys.argv) == 4 and sys.argv[1] == "select":
            identifier, received = inventory(Path(sys.argv[2]))
            evaluated = datetime.datetime.now(datetime.timezone.utc)
            Path(sys.argv[3]).write_text(json.dumps({
                "schemaVersion": 1, "snapshotId": identifier,
                "snapshotReceivedAt": rfc3339(received), "evaluatedAt": rfc3339(evaluated),
            }, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
            print(identifier)
            return 0
        if len(sys.argv) == 4 and sys.argv[1] == "preflight":
            recovery_point(Path(sys.argv[2]), Path(sys.argv[3]))
            return 0
        if len(sys.argv) == 7 and sys.argv[1] == "evidence":
            result = evidence(Path(sys.argv[2]), sys.argv[3], Path(sys.argv[4]), sys.argv[5])
            Path(sys.argv[6]).write_text(
                json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n",
                encoding="utf-8",
            )
            return 0
        raise ValueError("wal_recovery_evidence_usage")
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError) as error:
        print(f"wal_recovery_evidence_failed:{error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
