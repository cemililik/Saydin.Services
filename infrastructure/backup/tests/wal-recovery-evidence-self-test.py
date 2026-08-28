#!/usr/bin/env python3
from __future__ import annotations

import datetime
import importlib.util
import json
import os
from pathlib import Path
import tempfile

ROOT = Path(__file__).resolve().parents[3]
HELPER = ROOT / "infrastructure/backup/wal-recovery-evidence.py"
spec = importlib.util.spec_from_file_location("wal_recovery_evidence", HELPER)
if spec is None or spec.loader is None:
    raise SystemExit("wal_recovery_evidence_self_test_failed:load")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def stamp(epoch: int) -> str:
    return datetime.datetime.fromtimestamp(epoch, datetime.timezone.utc).isoformat().replace(
        "+00:00", "Z"
    )


def fixture(root: Path, *, source: int, observed: int, received: int) -> tuple[Path, Path, str]:
    identifier = "a" * 64
    inventory = root / "snapshots.json"
    inventory.write_text(json.dumps([{
        "id": identifier, "time": stamp(received), "tags": ["saydin", "wal", "wal-observation"]
    }]), encoding="utf-8")
    selection = root / "selection.json"
    selection.write_text(json.dumps({
        "schemaVersion": 1,
        "snapshotId": identifier,
        "snapshotReceivedAt": stamp(received),
        "evaluatedAt": stamp(int(datetime.datetime.now(datetime.timezone.utc).timestamp())),
    }), encoding="utf-8")
    wal = root / "wal"
    wal.mkdir()
    segment = "000000010000000000000001"
    segment_path = wal / segment
    segment_path.write_bytes(b"wal")
    os.utime(segment_path, (source, source))
    (wal / ".saydin-wal-observation").write_text(json.dumps({
        "schemaVersion": 1,
        "segment": segment,
        "segmentSourceTimestamp": source,
        "observedTimestamp": observed,
        "snapshotIncludesSegment": True,
        "serverTimeline": 1,
        "serverLsn": "0/01000000",
        "walSegmentSize": "16MB",
        "serverWalSegment": segment,
        "serverPreviousWalSegment": "000000010000000000000000",
    }), encoding="utf-8")
    return selection, wal, identifier


def rejected(*args: object) -> bool:
    try:
        module.evidence(*args)
    except (OSError, ValueError, json.JSONDecodeError):
        return True
    return False


def main() -> int:
    now = int(datetime.datetime.now(datetime.timezone.utc).timestamp())
    target = stamp(now - 86400)
    with tempfile.TemporaryDirectory(prefix="saydin-wal-evidence-") as raw:
        root = Path(raw)
        inventory, wal, identifier = fixture(
            root, source=now - 86400, observed=now - 60, received=now - 30
        )
        value = module.evidence(inventory, identifier, wal, target)
        assert value["currentRecoveryPointAgeSeconds"] <= 900
        assert value["guaranteedRecoveryPointAt"] == stamp(now - 360)

    with tempfile.TemporaryDirectory(prefix="saydin-wal-evidence-stale-") as raw:
        root = Path(raw)
        inventory, wal, identifier = fixture(
            root, source=now - 86400, observed=now - 2400, received=now - 2300
        )
        assert rejected(inventory, identifier, wal, target)

    with tempfile.TemporaryDirectory(prefix="saydin-wal-evidence-future-") as raw:
        root = Path(raw)
        inventory, wal, identifier = fixture(
            root, source=now + 60, observed=now + 60, received=now - 1
        )
        assert rejected(inventory, identifier, wal, target)

    with tempfile.TemporaryDirectory(prefix="saydin-wal-evidence-link-") as raw:
        root = Path(raw)
        inventory, wal, identifier = fixture(
            root, source=now - 120, observed=now - 60, received=now - 30
        )
        (wal / "unsafe").symlink_to(wal / "000000010000000000000001")
        assert rejected(inventory, identifier, wal, target)

    with tempfile.TemporaryDirectory(prefix="saydin-wal-evidence-behind-") as raw:
        root = Path(raw)
        inventory, wal, identifier = fixture(
            root, source=now - 120, observed=now - 60, received=now - 30
        )
        marker = wal / ".saydin-wal-observation"
        value = json.loads(marker.read_text(encoding="utf-8"))
        value.update({
            "serverLsn": "0/03000000",
            "serverWalSegment": "000000010000000000000003",
            "serverPreviousWalSegment": "000000010000000000000002",
        })
        marker.write_text(json.dumps(value), encoding="utf-8")
        assert rejected(inventory, identifier, wal, target)

    print("wal_recovery_evidence_self_test_passed:quiet,stale,future,symlink,receiver-behind")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
