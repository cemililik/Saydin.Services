# Database login credential lifecycle

This runbook applies to the six managed application login purposes (`migrator`,
`api`, `ingestion`, `calendar-importer`, `exporter`, and `audit`). Backup logins
use [backup-login-renewal.md](backup-login-renewal.md); never substitute an
application-login command for the physical-replication procedure.

The role-bootstrap service mounts the external bootstrap secret volume at
`/run/saydin-secrets/private`. Its normal `ensure` command reads stable aliases
named `<purpose>-current`; a versioned candidate exists only during a controlled
rotation. Consumer volumes continue to expose their password as `password`
(`data-repair` uses `ingestion-current` and `audit-current`). Passwords must be
owner-only regular files. They must never appear in argv, environment variables,
SQL, command output, tickets, or shell history.

Use a deployment lock for the whole procedure. Do not run a release deployment
while a versioned candidate file is present: the production private-material
gate intentionally accepts only the stable current aliases.

## Prepare the operator shell

Work from the repository root on the production host. The environment file
contains identifiers, not secret values, and must itself remain owner-only.

```sh
export SAYDIN_PRODUCTION_ENV_FILE=/absolute/operator-owned/production.env
test -f "$SAYDIN_PRODUCTION_ENV_FILE"
test "$(stat -c '%a' "$SAYDIN_PRODUCTION_ENV_FILE")" = 600
set -a
. "$SAYDIN_PRODUCTION_ENV_FILE"
set +a
```

All lifecycle commands use the same target-bound arguments. The examples below
spell them out so the procedure can be copied without an undocumented wrapper:

```sh
role_bootstrap() {
  docker compose \
    --project-name saydin-production \
    --env-file "$SAYDIN_PRODUCTION_ENV_FILE" \
    -f infrastructure/deployment/compose.production.yml \
    run --rm --no-deps database-role-bootstrap "$@" \
    --admin-connection-file /run/saydin-secrets/private/admin-connection \
    --deployment-id "$SAYDIN_DEPLOYMENT_ID" \
    --target-database "$SAYDIN_DATABASE" \
    --system-identifier-sha256 "$SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" \
    --role-prefix "$SAYDIN_DATABASE_ROLE_PREFIX" \
    --timescaledb-version 2.16.1 \
    --uuid-ossp-version 1.1 \
    --backup-v1-valid-until "$SAYDIN_BACKUP_V1_VALID_UNTIL" \
    --connect-timeout-seconds 10 \
    --lock-timeout-seconds 30 \
    --statement-timeout-seconds 30 \
    --total-timeout-seconds 120
}
```

The production database must already be running and reachable on the
`backup-db` network. `--no-deps` is deliberate: an incident command must not
start or recreate PostgreSQL.

## Rotate to the next version

Choose exactly the next version in `1..32`; skipping or going backwards is
rejected. In the secret control plane, create a fresh candidate such as
`private/api-v2` in the bootstrap volume with uid 1001 and mode `0400` or `0600`.
Do not copy it into a consumer volume yet.

```sh
role_bootstrap rotate \
  --login api \
  --login-version 2 \
  --password-file /run/saydin-secrets/private/api-v2
```

Success proves the new role marker, attributes, memberships, ACLs, SQL denies,
and an actual password authentication probe. Then perform one atomic cutover:

1. Copy the candidate secret to the affected consumer volume's
   `private/password` file and to bootstrap `private/api-current`, preserving
   uid/mode and using the secret control plane's atomic replace operation.
2. Change `SAYDIN_API_LOGIN` to the exact new role name and
   `SAYDIN_API_LOGIN_VERSION` to `2` in the production environment file. Use the
   equivalent pair for another purpose.
3. Recreate only the affected consumer, wait for its health/readiness gate, and
   confirm the old role has no new sessions.
4. Run the production asset/private-material validation and `verify` below.

The required environment pairs are:

| Purpose | Login name | Login version | Bootstrap current alias |
|---|---|---|---|
| migrator | `SAYDIN_MIGRATOR_LOGIN` | `SAYDIN_MIGRATOR_LOGIN_VERSION` | `migrator-current` |
| api | `SAYDIN_API_LOGIN` | `SAYDIN_API_LOGIN_VERSION` | `api-current` |
| ingestion | `SAYDIN_INGESTION_LOGIN` | `SAYDIN_INGESTION_LOGIN_VERSION` | `ingestion-current` |
| calendar-importer | `SAYDIN_CALENDAR_IMPORTER_LOGIN` | `SAYDIN_CALENDAR_IMPORTER_LOGIN_VERSION` | `calendar_importer-current` |
| exporter | `SAYDIN_EXPORTER_LOGIN` | `SAYDIN_EXPORTER_LOGIN_VERSION` | `exporter-current` |
| audit | `SAYDIN_AUDIT_LOGIN` | `SAYDIN_AUDIT_LOGIN_VERSION` | `audit-current` |

When rotating ingestion or audit, also atomically update the corresponding
`data-repair` alias (`ingestion-current` or `audit-current`) before the next
operator run.

```sh
role_bootstrap verify
./infrastructure/deployment/validate-production-assets.sh \
  "$SAYDIN_PRODUCTION_ENV_FILE"
```

If the new consumer fails before retirement, point the consumer back to the old
login and old consumer secret. Keep the bootstrap `*-current` alias on the
highest database version: `ensure` always authenticates the highest managed
version. Diagnose or rotate forward; do not manually drop the candidate role.

## Reset the current version

Use this only when the role identity remains valid but its password is
compromised. The target must be the highest managed version. Create a temporary
owner-only candidate such as `private/api-v2-replacement` and run:

```sh
role_bootstrap reset-password \
  --login api \
  --login-version 2 \
  --password-file /run/saydin-secrets/private/api-v2-replacement
```

After success, atomically replace both the consumer password and
`private/api-current`, recreate the consumer, run `verify`, and delete the
temporary replacement file. The previous password must fail. If the final
authentication probe fails, keep the replacement as the only candidate and
diagnose by stable failure code; never print or restore either password.

## Retire and drain an old version

Retire only after the replacement is healthy, is the highest managed version,
and `verify` succeeds.

```sh
role_bootstrap retire \
  --login api \
  --login-version 1 \
  --replacement-version 2 \
  --drain-timeout-seconds 30
```

Retirement first commits `NOLOGIN`, preventing new authentication while keeping
the exact marker and memberships. It then waits for a bounded drain interval; it
never terminates sessions. `retired_login_sessions_active` means the role is
safely `NOLOGIN` but an old session remains. Let the owning workload close the
session and rerun the same command. A retry recognizes that exact draining
state, revokes only expected memberships, drops only the old role, and verifies
the remaining graph before commit.

After success:

1. Confirm old authentication is rejected, replacement authentication works,
   `role_bootstrap verify` is green, and no old session or role remains.
2. Delete the temporary versioned candidate from the bootstrap secret volume;
   retain only the exact stable aliases accepted by
   `validate-private-material.py`.
3. Run `validate-production-assets.sh` again, release the deployment lock, and
   record only role names, versions, timestamps, and stable result codes.

Do not manually change markers, grants, role attributes, `pg_hba.conf`, or
`pg_authid`. A marker, ACL, or attribute mismatch is a security incident and
must remain fail-closed.
