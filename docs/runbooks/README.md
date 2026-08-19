# Production runbooks

These runbooks are defensive response procedures. Commands must target the release
manifest's exact deployment ID and signed digest; do not substitute the development
Compose file. Never print, copy, or pass secrets through environment variables or
argv. Database inspection uses the managed audit/control-plane identity appropriate
to the step—never an API/ingestion login and never an interactive superuser fallback.

Common order:

1. acknowledge the alert and record alert fingerprint, deployment ID, release digest,
   start time and incident owner;
2. establish whether the fault is external, runtime, data-plane or release-related;
3. preserve logs, metrics and DQA/backup evidence before changing state;
4. use a signed known-good digest or documented forward-fix; never rebuild on-host;
5. verify the alert resolves and attach measured recovery evidence.

Targets: RPO 15 minutes, RTO 120 minutes. Production promotion remains blocked until
the backup/PITR drill and the alert routing game-day prove these objectives.
