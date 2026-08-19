# Authoritative calendar acquisition and release

Trigger: TCMB active coverage does not include Istanbul-yesterday, BIST Pay active coverage
is below 45 days, the coverage metric is missing, or acquisition/promotion fails.

1. Check `saydin_market_calendar_coverage_horizon_days` for the bounded calendar label and
   query the active release pointer plus its sealed `coverage_through`. A worker restart cannot
   change this durable state.
2. Inspect the relevant systemd timer and acquisition service. One global `flock`, a 15-minute
   timeout and three bounded attempts are expected. Do not weaken URI/media/size/parser checks
   to make a candidate pass.
3. Materialize a new plan from the reviewed example under `infrastructure/calendar/plans/`.
   TCMB refreshes the exact annual and current-month archive sources daily; BIST refreshes the
   official index plus next-year Pay Piyasası PDF annually. Use a new snapshot set id.
4. Acquisition must end in the owner-only quarantine root. It must not have DB credentials and
   must not call `import` or `activate`. Review raw-byte hashes, official URI provenance and the
   normalized diff. A missing expected day means coverage must not advance.
5. A reviewer identity separate from acquisition signs `review-envelope.json`. Run
   `infrastructure/calendar/promote-reviewed-bundle.sh`; it verifies the detached signature,
   envelope hashes and a Docker `--network none` replay before atomic promotion.
6. Only the separately authorized calendar-importer identity may import/seal and CAS-activate
   the promoted release. Capture old/new release ids and hashes in the change record. Never
   hand-edit active calendar rows.

Resolved when the active sealed TCMB release covers Istanbul-yesterday, BIST has at least 45
days remaining, both metric series are present, and ingestion windows no longer emit
`calendar_not_ready`.
