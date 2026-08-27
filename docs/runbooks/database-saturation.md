# PostgreSQL unavailable or saturated

Trigger: `SaydinPostgresUnavailable` or high connection budget.

1. Freeze deployment/migration jobs and capture exporter, host disk/memory and Postgres
   process state through the managed exporter/control-plane identities.
2. Confirm API/ingestion/migrator/exporter backend identities and pool counts. Any
   admin/superuser runtime backend is a security incident.
3. Check locks, long transactions, Timescale background jobs, disk fullness and WAL
   archive health. Do not terminate sessions by broad pattern.
4. Resolve exact offending session/job; preserve ingestion fence and chunk/schema ACLs.
5. If storage integrity or host loss is suspected, stop writes and follow the isolated
   PITR procedure in `backup-failure.md`.

Resolved when `pg_up=1`, connections remain below the approved budget for 30 minutes,
WAL archiving is current and migrator verify-only plus DQA pass.
