# API errors and activity-log loss

Trigger: `SaydinApiErrorBudgetBurn` or `SaydinActivityLogLoss`.

1. Group by bounded route/status/outcome labels; never add device IDs, IPs, scenario IDs
   or raw exception text as labels.
2. Correlate the increase with release digest, Redis/DB availability and activity writer
   queue/write counters.
3. Preserve structured logs and trace IDs. Verify raw installation credentials, IPs,
   HMAC material and Redis/DB secrets are absent before sharing evidence.
4. If activity writes are being lost, stop new promotion and allow the configured API
   stop grace period to drain; do not bypass the managed DB role.
5. Roll back only to a signed schema-compatible digest or ship a reviewed forward-fix.

Resolved when the 5xx ratio is below one percent and all activity-loss counters remain
flat for 30 minutes.
