# Authoritative calendar acquisition and release

Trigger: BIST Pay active coverage is below 45 days, TCMB no longer covers Istanbul-yesterday or
the latest proven eligible publication day, the coverage metric is missing, or acquisition/
promotion fails. TCMB is retrospective and intentionally has no 45-day expiry warning.

1. Check `saydin_market_calendar_coverage_horizon_days` for the bounded calendar label and
   query the active release pointer plus its sealed `coverage_through`. A worker restart cannot
   change this durable state.
2. Inspect the relevant systemd timer and acquisition service. The service must run as the
   dedicated uid/gid 1001 identity with its lock below `/var/lib/saydin/calendar/locks`.
   One global `flock`, a 15-minute timeout, bounded container resources and three attempts are
   expected. TCMB plan materialization is deterministic: an identical cutoff is a no-op and a
   newer cutoff atomically replaces the stable daily plan.
   Do not weaken URI/media/size/parser checks to make a candidate pass.
3. For TCMB, compare requested provider cutoff (16:30 Europe/Istanbul) with the candidate's
   `coverageThrough` and final `observation_expected=true` row. Coverage must be clamped to an
   actual monthly-archive publication; an unproven future day is a fail-closed release defect.
4. Before promotion, verify the SHA-256 of the reviewer public key equals
   `SAYDIN_CALENDAR_REVIEWER_PUBLIC_KEY_SHA256`, and that
   `SAYDIN_CALENDAR_VERIFY_CANDIDATE` is the installed absolute regular executable. Never
   substitute an ad-hoc verifier beside the candidate.
5. TCMB's daily plan is materialized automatically; for BIST, materialize a new plan from the
   reviewed example under `infrastructure/calendar/plans/`. TCMB refreshes the exact annual and
   current-month archive sources daily; BIST refreshes the official index plus next-year Pay
   Piyasası PDF annually. Use a new snapshot set id.
6. Acquisition must end in the owner-only quarantine root. It must not have DB credentials and
   must not call `import` or `activate`. Review raw-byte hashes, official URI provenance and the
   normalized diff. A missing expected day means coverage must not advance.
7. A reviewer identity separate from acquisition signs `review-envelope.json`. Run
   `infrastructure/calendar/promote-reviewed-bundle.sh`; it verifies the detached signature,
   envelope hashes and a Docker `--network none` replay before atomic promotion.
8. Only the separately authorized calendar-importer identity may import/seal and CAS-activate
   the promoted release. Capture old/new release ids and hashes in the change record. Never
   hand-edit active calendar rows.
9. If a closure or missing publication is announced after a release was sealed, create and
   activate a corrected release first. Recover each permanently blocked old-release window only
   with a signed DataRepair schema-v2 `requeue_permanent_window` plan; the next claim must prove
   and bind the corrected active release. A plain schema-v1 requeue deliberately preserves the
   old binding and is not a recovery path for this incident.

Resolved when the active sealed TCMB release covers Istanbul-yesterday, BIST has at least 45
days remaining, both metric series are present, and ingestion windows no longer emit
`calendar_not_ready`.
