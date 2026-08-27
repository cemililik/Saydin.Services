#!/usr/bin/env python3
"""Select the newest exact Restic base snapshot at or before an RFC3339 target."""

from __future__ import annotations

import datetime
import json
import re
import sys
from pathlib import Path

RFC3339 = re.compile(
    r"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})$"
)
HEX64 = re.compile(r"^[0-9a-f]{64}$")
MAX_BYTES = 8 * 1024 * 1024


def instant(value: object) -> datetime.datetime:
    if not isinstance(value, str) or not (match := RFC3339.fullmatch(value)):
        raise ValueError("snapshot_time_invalid")
    base, fraction, zone = match.groups()
    fractional = f".{fraction[:6]}" if fraction else ""
    normalized_zone = "+00:00" if zone == "Z" else zone
    parsed = datetime.datetime.fromisoformat(base + fractional + normalized_zone)
    if parsed.tzinfo is None:
        raise ValueError("snapshot_time_not_utc_aware")
    return parsed.astimezone(datetime.timezone.utc)


def main() -> int:
    if len(sys.argv) != 3:
        print("restore_snapshot_selector_usage", file=sys.stderr)
        return 64
    try:
        path = Path(sys.argv[1])
        with path.open("rb") as stream:
            payload = stream.read(MAX_BYTES + 1)
        if len(payload) > MAX_BYTES:
            raise ValueError("snapshot_inventory_too_large")
        snapshots = json.loads(payload)
        target = instant(sys.argv[2])
        if not isinstance(snapshots, list):
            raise ValueError("snapshot_inventory_invalid")
        eligible: list[tuple[datetime.datetime, str]] = []
        seen: set[str] = set()
        for item in snapshots:
            if not isinstance(item, dict) or set(item).isdisjoint({"id", "time"}):
                raise ValueError("snapshot_entry_invalid")
            identifier = item.get("id")
            if not isinstance(identifier, str) or not HEX64.fullmatch(identifier) or identifier in seen:
                raise ValueError("snapshot_id_invalid")
            seen.add(identifier)
            observed = instant(item.get("time"))
            if observed <= target:
                eligible.append((observed, identifier))
        if not eligible:
            raise ValueError("restore_base_before_target_missing")
        print(max(eligible)[1])
        return 0
    except (OSError, ValueError) as exc:
        print(f"restore_snapshot_selector_failed:{exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
