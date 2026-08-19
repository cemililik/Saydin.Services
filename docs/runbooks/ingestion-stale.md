# Ingestion stale or failing

Trigger: daily 26-hour or monthly 40-day freshness SLO, durable failure streak,
provider failure, missing series or calendar-not-ready.

1. Confirm the ingestion profile is intentionally enabled and at least one worker is
   enabled in its private production config.
2. Inspect bounded `source`/`cadence`/`outcome` labels. Compare
   `last_attempt_timestamp_seconds`, `last_success_timestamp_seconds`, `lag_seconds`,
   `failure_streak`, attempt duration and accepted/rejected record histograms. These gauges
   hydrate from durable `ingestion_jobs`/terminal `ingestion_windows` on restart; do not clear
   an alert by restarting a worker.
3. Confirm `last_success` advanced only for a committed `succeeded` or
   `expected_no_data` window. Retryable/permanent/partial/cancelled outcomes must not advance it.
   Inspect lease expiry and market calendar readiness. Do not log provider URLs, headers,
   bodies or keys.
4. Verify the running worker has the exact ingestion login and no admin/other-capability
   membership. Do not bypass the window lease/write fence.
5. Classify provider auth/schema errors as permanent and transport/429/5xx according to
   the existing bounded resilience contract.
6. Requeue/refetch only through the reviewed provenance workflow; never hand-edit final
   observations or attribution ledgers.

Resolved when every enabled source has a successful authoritative observation inside
its cadence-specific freshness window, durable lag is inside SLO, failure streak is zero
and DQA is clean.
