# Production deployment baseline

This directory is the canonical production runtime baseline. It is independent
from the root development Compose file: production must never fall back to
`docker-compose.yml`, host builds, mutable tags, or development defaults.

## Release blockers

The manifest is intentionally fail-closed. A deployment is not releasable until:

1. terminal migration 022 is frozen and its exact SHA-256 is embedded in the
   Migrator/DQA trust root;
2. every first- and third-party image variable resolves to a reviewed manifest-list
   `@sha256:` digest;
3. the API installation keyring, security-limiter HMAC, Redis credentials, provider
   credentials, and all managed database credentials have been materialized into
   their exact private external volumes;
4. the encrypted off-host base/WAL backup job and restore drill are executable;
5. Alertmanager receiver material is rendered outside Git into its private volume.
6. the private Tempo/Loki forensic backends, Collector durable queue/retry policy and
   retention volumes have passed the production asset validator.

The operating assumptions are **RPO 15 minutes** and **RTO 120 minutes**. These are
release objectives, not achieved claims, until a measured isolated PITR drill passes.

The backup profile requires the dedicated versioned physical-replication login from
the frozen role contract; admin, migrator, exporter or application credentials are not
acceptable substitutes. `deploy-release.sh` installs and verifies the exact managed
`hostssl` replication/ordinary-SQL-reject HBA block before post-bootstrap, then requires
real SQL-deny and immediate `pg_basebackup` acceptance; CI additionally runs bounded
`pg_receivewal` concurrently with fetch-mode base backup under the role's exact
two-connection limit. Production retains an exact 8 GB fetch WAL window and caps the
physical slot at 8 GB. Object storage uses a short-lived web-identity token and
KMS-materialized Restic key; no long-lived cloud access key is accepted.

## Secret and configuration contract

No secret may occur in Compose environment, command/argv, an image reference, a
tracked config, or a Docker label. A root-only control-plane materializes external
volumes before Compose validation. Runtime consumers see only their own read-only
volume. The bootstrap one-shot is the only service that may see the admin connection
and all managed-role bootstrap passwords.

Required private directories are `0700`; files are regular, owner-exact, link-count
one and `0400` or `0600`. Run `validate-private-material.py` inside the materializer
namespace before attaching a volume. It emits stable codes and never prints paths or
values.

The PostgreSQL material contains `password`, `server.crt`, and `server.key`. The
bootstrap material additionally contains only `admin-connection`, the six application
passwords, and `backup-v1`; `admin-connection` must target `postgres-backup:5432` with
`SSL Mode=Require`. The audit material contains `password`, `evidence-hmac`, and the
exact SubjectPublicKeyInfo `evidence-public.pem`; a production evidence private key is
forbidden. DQA signs through OCI instance principal and the allowlisted KMS key/version.

API and ingestion require private `appsettings.Production.json` files because their
current Redis/provider configuration is read by the standard .NET configuration
provider. API private config contains only its Redis connection string; ingestion
private config contains provider credentials and the explicitly enabled worker set.
These files are mounted over `/app/appsettings.Production.json`; they are never copied
into an image or stored in Git.

Redis consumes `/run/saydin-secrets/private/redis.conf`; its password is therefore not
present in Docker inspect or argv. The config must set AOF, an explicit maxmemory below
the container limit, and `maxmemory-policy noeviction`. Evicting quota/security-limiter
keys would weaken enforcement; write pressure must instead surface as fail-closed 503.
The Redis exporter gets a separate password file.

`SAYDIN_CADDY_IMAGE` is the digest of `Dockerfile.caddy`, not the upstream image
directly. The upstream Caddy binary requests `cap_net_bind_service`; Linux refuses that
exec under `cap_drop: ALL` plus `no-new-privileges`. The derived image strips only this
file capability and listens on 8080/8443 behind host mappings for 80/443.

API host filtering is an exact two-entry allowlist: the operator-selected public DNS
name and the private Compose alias `saydin-api`. Caddy preserves the public Host header;
Prometheus and the container liveness probe use the private alias. Wildcards, arbitrary
internal names and a liveness request without its explicit Host header fail validation.

## Validation

Copy `production.env.example` outside the repository, replace every placeholder with
nonsecret deployment metadata/digests/volume names, and run:

```sh
infrastructure/deployment/validate-production.sh /absolute/path/production.env
```

The command renders all profiles twice with distinct Compose project names, validates
the full JSON model, and runs mutation negatives for environment, host, limiter,
digest, raw-secret, port and hardening drift. It does not start services or contact a
production system.

The external blackbox target volume contains one Prometheus file-SD JSON document,
for example a target list containing the chosen public HTTPS health URL. The file is
nonsecret but remains operator-owned so the repository has no guessed domain.

## Deployment order

1. Verify the signed release and materialize/validate exact private volumes.
2. Start PostgreSQL/Redis/OTel; install and reload the managed backup HBA block on the
   dedicated private `/28` backup network.
3. Run phase-aware pre-bootstrap and Migrator (24 migrations). If the backup role was
   deferred, rerun bootstrap after migration; in every case require final
   `backup_postbootstrap_required=false` from bootstrap and Migrator verify-only.
4. Run physical-backup ordinary-SQL deny, immediate base backup, WAL scheduler, and OCI
   KMS-backed DQA; archive signed evidence.
5. Start telemetry/exporters and API; run readiness and contract smoke.
6. Start ingestion only with its explicit profile and a verified worker.
7. Admit Caddy traffic only after smoke succeeds.

Outbound access is split as narrowly as Compose permits: only ingestion joins
`provider-egress`, only Alertmanager joins `alert-egress`, only DQA joins `kms-egress`,
and only backup joins `backup-egress`. PostgreSQL and the backup jobs share a dedicated
internal `backup-db` network solely for TLS physical replication; normal consumers use
`data`. Host firewall/DNS policy must restrict each egress network to its approved
destinations.

Calendar and DQA are one-shot profiles. WAL archiving and the daily base-backup
scheduler are continuous; deployment additionally requires one immediate verified
base backup. A rollback changes only application
digests to a previously signed schema-compatible release; migrations are never
automatically reversed.
