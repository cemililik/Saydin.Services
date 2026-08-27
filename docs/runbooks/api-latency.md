# API latency

Trigger: `SaydinApiLatencyHigh`.

1. Compare p50/p95/p99 by bounded route with DB connections, Redis health, host CPU,
   memory and disk pressure.
2. Distinguish cache degradation from DB saturation; never disable the distributed
   security limiter to improve latency.
3. Check for an N+1/query-plan regression against the current release digest and recent
   migration state.
4. Apply bounded concurrency/resource changes only through reviewed manifest updates.

Resolved when p95 is below one second for 30 minutes without increased 5xx or dropped
activity records.
