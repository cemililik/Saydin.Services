# Host disk pressure

Trigger: less than 15 percent free on a non-ephemeral filesystem.

1. Identify the exact filesystem and growth source using read-only inspection.
2. Check PostgreSQL data/WAL, Prometheus retention, container logs, backup staging and
   orphaned release artefacts. Do not delete database/WAL files manually.
3. Verify a recent encrypted off-host recovery point before any retention action.
4. Reduce data only through the owner-approved retention/lifecycle policy; log rotation
   and Prometheus retention remain bounded by the production manifest.

Resolved when free space exceeds 25 percent, backup/WAL health is current and no
unbounded growth remains.
