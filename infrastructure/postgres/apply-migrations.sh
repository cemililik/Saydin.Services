#!/usr/bin/env bash
# Deprecated fail-closed compatibility entrypoint.
#
# The former psql loop had no durable lock, split migration body/tracking into
# separate commits, and let shell migrations choose a second connection target.
# It must never be used after the always-run Saydin.DatabaseMigrator control
# plane was introduced. Keeping this tombstone gives old automation an explicit
# non-zero failure instead of silently using unsafe semantics.
set -euo pipefail

echo "apply-migrations.sh is retired; run the Saydin.DatabaseMigrator one-shot job instead." >&2
echo "Compose: docker compose run --rm database-migrator" >&2
exit 64
