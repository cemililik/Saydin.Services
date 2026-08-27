# Backup login validity renewal

The backup login is deliberately time-bounded. Production admission accepts a validity window of 45 through 93 days, measured by the PostgreSQL clock. The warning begins below 30 days, leaving at least 15 days to complete a normal signed release before expiry.

## Normal v1 extension

1. Query the database clock and the current role value as `saydin_admin`:

   ```sql
   SELECT clock_timestamp(), rolvaliduntil
   FROM pg_catalog.pg_roles
   WHERE rolname = '<role-prefix>_backup_login_v1';
   ```

2. Choose a new whole-second UTC value 60 days after the database clock. Do not use the operator host clock. Update `SAYDIN_BACKUP_V1_VALID_UNTIL` in the non-secret production configuration and run the normal signed deployment. Do not run `ALTER ROLE`, edit HBA by hand, or bypass RoleBootstrap.
3. The deployment is forward-only: RoleBootstrap extends the existing v1 login, verifies the exact backup HBA rules and physical replication authentication, and Migrator proves `backup_postbootstrap_required=false`. The deploy then proves the actual `pg_roles.rolvaliduntil` equals the requested value, proves the same secret can perform physical replication but cannot open a SQL session, and only then publishes `saydin_backup_login_valid_until_timestamp_seconds`.
4. Confirm WAL streaming and the immediate verified base backup pass. Confirm the metric equals `floor(extract(epoch from rolvaliduntil))` and that the missing, expiring, and expired alerts are inactive.

If any gate fails, abort the deployment and retain the last known-good release and role. Retrying with corrected configuration is safe; shortening validity and direct database rollback are forbidden. If compromise is suspected, stop backup admission and follow incident response rather than extending the compromised credential.

## Version rotation

The production runtime currently supports v1 only. Do not attempt a v1-to-v2 cutover until v2 is wired end-to-end through RoleBootstrap, the exact HBA contract, secret mounts, the backup runtime, physical-auth/SQL-deny tests, metrics, alerts, and forward-only deployment validation. There is no supported rollback from a completed version cutover to an older credential; abort before activation if any pre-activation check fails.
