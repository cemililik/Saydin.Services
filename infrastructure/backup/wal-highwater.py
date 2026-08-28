#!/usr/bin/env python3
"""Convert a PostgreSQL physical-replication high-water LSN to WAL segment names."""

from __future__ import annotations

import re
import sys

LSN = re.compile(r"^([0-9A-F]+)/([0-9A-F]{1,8})$")
SIZE = re.compile(r"^([1-9][0-9]*)(kB|MB|GB)$")


def segment_names(timeline_text: str, lsn_text: str, size_text: str) -> tuple[str, str]:
    if not timeline_text.isdigit() or not (1 <= int(timeline_text) <= 0xFFFFFFFF):
        raise ValueError("wal_highwater_timeline_invalid")
    match = LSN.fullmatch(lsn_text)
    size_match = SIZE.fullmatch(size_text)
    if match is None or size_match is None:
        raise ValueError("wal_highwater_format_invalid")
    multiplier = {"kB": 1024, "MB": 1024**2, "GB": 1024**3}[size_match.group(2)]
    size = int(size_match.group(1)) * multiplier
    if size < 1024**2 or size > 1024**3 or size & (size - 1) or (1 << 32) % size:
        raise ValueError("wal_highwater_segment_size_invalid")
    lsn = (int(match.group(1), 16) << 32) + int(match.group(2), 16)
    segment_number = lsn // size
    segments_per_log = (1 << 32) // size

    def name(number: int) -> str:
        log, segment = divmod(number, segments_per_log)
        return f"{int(timeline_text):08X}{log:08X}{segment:08X}"

    current = name(segment_number)
    previous = name(max(0, segment_number - 1))
    return current, previous


def main() -> int:
    try:
        if len(sys.argv) != 4:
            raise ValueError("wal_highwater_usage")
        print("|".join(segment_names(*sys.argv[1:])))
        return 0
    except ValueError as error:
        print(f"wal_highwater_failed:{error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
