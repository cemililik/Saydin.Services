# Scenario integrity migration gate

Use this gate before a production database first crosses migration 018. The migration is
deliberately fail-closed and never deletes or rewrites existing scenarios. Do not discover an
over-cap user in the release transaction: complete this read-only assessment before approving
the release.

All commands run from a root-owned maintenance host against the exact database named in the
signed release. Use the managed control-plane login and a root-owned `PGPASSFILE`; never put a
password in a URI, environment value, command argument or transcript. Record the deployment ID,
database name, SHA-256 of `pg_control_system().system_identifier`, release digest, change ticket,
operator and UTC start time. Stop API writers for the archive/apply phase; ingestion does not
write `saved_scenarios` but should remain stopped with the release.

## Read-only assessment

Run the following in `psql -X --no-psqlrc --set ON_ERROR_STOP=1`. Preserve its output with the
release evidence. A non-zero value in any column blocks migration 018.

```sql
WITH scenario_preflight AS (
    SELECT
        count(*) FILTER (
            WHERE extra_data IS NOT NULL
              AND jsonb_typeof(extra_data) NOT IN ('object', 'null')) AS non_object_extra_data,
        count(*) FILTER (
            WHERE extra_data IS NOT NULL
              AND octet_length(extra_data::text) > 8192) AS oversized_extra_data,
        count(*) FILTER (
            WHERE type = 'dca' AND quantity_unit <> 'try') AS invalid_dca_unit
      FROM public.saved_scenarios
), over_cap AS (
    SELECT count(*) AS users_over_cap,
           coalesce(sum(scenario_count - 100), 0) AS rows_over_cap
      FROM (
          SELECT user_id, count(*) AS scenario_count
            FROM public.saved_scenarios
           GROUP BY user_id
          HAVING count(*) > 100
      ) AS users
)
SELECT current_database() AS database_name,
       scenario_preflight.*,
       over_cap.users_over_cap,
       over_cap.rows_over_cap
  FROM scenario_preflight CROSS JOIN over_cap;
```

If object, size or DCA-unit violations are present, stop. Do not normalize financial payloads
or change units in this procedure; route them through the signed DataRepair process with product
owner approval. If only `users_over_cap` is non-zero, choose one of these explicit outcomes:

- retain all live rows and keep the release blocked; or
- archive the deterministic rows beyond the newest 100 and obtain independent deletion approval.

There is no automatic retention choice.

## Produce the immutable archive candidate

Use an encrypted, root-only filesystem outside the repository and `/tmp`. Verify the directory
is an ordinary directory, is not a symlink and has mode `0700`. Resolve and inspect a new
absolute archive path; abort if it already exists. Replace the literal absolute placeholder
below only after that check. (`\copy` intentionally does not interpolate psql variables.) With
writers stopped, start one `psql` session and hold the table lock until the `\copy` has completed:

```sql
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '15min';
LOCK TABLE public.saved_scenarios IN SHARE ROW EXCLUSIVE MODE;

CREATE TEMP TABLE scenario_archive_candidate ON COMMIT DROP AS
WITH ranked AS (
    SELECT scenario.*,
           row_number() OVER (
               PARTITION BY user_id ORDER BY created_at DESC, id DESC) AS scenario_rank
      FROM public.saved_scenarios AS scenario
)
SELECT to_jsonb(ranked) - 'scenario_rank' AS document
  FROM ranked
 WHERE scenario_rank > 100
 ORDER BY user_id, created_at DESC, id DESC;

\copy scenario_archive_candidate(document) TO '/absolute/validated/encrypted/saved-scenarios.copy' WITH (FORMAT text)
SELECT count(*) AS archived_rows FROM scenario_archive_candidate;
ROLLBACK;
```

After the session returns successfully, set the archive file to `0400`, compute its SHA-256,
copy it to encrypted
off-host immutable storage, and record the hash, byte length and row count. A second operator
must review the assessment and candidate, then approve the exact tuple:

`change-ticket / database / system-identifier-sha256 / archive-sha256 / row-count`.

Do not treat possession of the archive file as approval. Do not proceed while the API is
running or if the target identity, file hash, count or approval differs.

## Apply the approved archive

Load the exact immutable candidate into a temporary one-column `jsonb` table in a new `psql`
session. Keep the transaction and table lock open for every statement below. The two set
comparisons prove that the candidate is still the complete set beyond the newest 100; the row
comparison proves that every exported row is byte-semantically the same PostgreSQL row. Any
concurrent or intervening change aborts before `DELETE`.

```sql
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '15min';
LOCK TABLE public.saved_scenarios IN SHARE ROW EXCLUSIVE MODE;

CREATE TEMP TABLE approved_scenario_archive (
    document jsonb NOT NULL,
    id uuid GENERATED ALWAYS AS ((document ->> 'id')::uuid) STORED,
    PRIMARY KEY (id)
) ON COMMIT DROP;
\copy approved_scenario_archive(document) FROM '/absolute/validated/encrypted/saved-scenarios.copy' WITH (FORMAT text)

DO $archive_admission$
BEGIN
    IF EXISTS (
        SELECT 1
          FROM approved_scenario_archive AS approved
          LEFT JOIN public.saved_scenarios AS live ON live.id = approved.id
         WHERE live.id IS NULL OR to_jsonb(live) IS DISTINCT FROM approved.document
    ) THEN
        RAISE EXCEPTION 'scenario archive row drift'
            USING ERRCODE = '23514';
    END IF;

    IF EXISTS (
        WITH expected AS (
            SELECT id
              FROM (
                  SELECT id,
                         row_number() OVER (
                             PARTITION BY user_id ORDER BY created_at DESC, id DESC) AS scenario_rank
                    FROM public.saved_scenarios
              ) AS ranked
             WHERE scenario_rank > 100
        )
        (SELECT id FROM expected EXCEPT SELECT id FROM approved_scenario_archive)
        UNION ALL
        (SELECT id FROM approved_scenario_archive EXCEPT SELECT id FROM expected)
    ) THEN
        RAISE EXCEPTION 'scenario archive candidate set drift'
            USING ERRCODE = '23514';
    END IF;
END
$archive_admission$;

WITH deleted AS (
    DELETE FROM public.saved_scenarios AS live
     USING approved_scenario_archive AS approved
     WHERE live.id = approved.id
     RETURNING live.id
)
SELECT count(*) AS deleted_rows FROM deleted;

COMMIT;
```

The second operator must witness the target identity and archive SHA immediately before this
transaction. Capture the exact `deleted_rows` result in the change record. Preserve the archive
and approval evidence for the financial-data retention period; never overwrite or reuse them.

## Release acceptance

Rerun the read-only assessment and require every numeric blocker field to be zero. Start the normal
signed release; do not invoke migration 018 manually. Require the migrator receipt to show the
trusted checksum for 018, then verify:

```sql
SELECT user_id, count(*)
  FROM public.saved_scenarios
 GROUP BY user_id
HAVING count(*) > 100;
```

The query must return no rows. Exercise the scenario list/page endpoints for one retained user,
confirm the release health gates, and attach the migrator receipt, assessment, archive approval
and post-release evidence to the change ticket.
