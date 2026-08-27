# Observability game-day

Run only in an isolated staging project using signed release digests. Record alert fire
time, route time, acknowledgement, recovery, resolve notification and runbook outcome.

| Injection | Expected alert | Maximum signal time |
|---|---|---:|
| Stop API process | `SaydinApiUnavailable` | 3 minutes |
| Return controlled 5xx traffic | `SaydinApiErrorBudgetBurn` | 15 minutes |
| Delay controlled API route | `SaydinApiLatencyHigh` | 20 minutes |
| Suppress both ingestion freshness metric families | `SaydinIngestionFreshnessMetricMissing` | 15 minutes |
| Suppress daily ingestion freshness | `SaydinDailyIngestionStale` | 26 hours + 15 minutes |
| Suppress monthly ingestion freshness | `SaydinMonthlyIngestionStale` | 40 days + 15 minutes |
| Stop PostgreSQL/Redis exporter connectivity | database/Redis unavailable | 4 minutes |
| Fill disposable filesystem below 15 percent free | `SaydinHostDiskPressure` | 20 minutes |
| Suppress backup checkpoint metric | backup missing/stale | 30 minutes |
| Use an expiring staging certificate fixture | `SaydinCertificateExpiring` | 30 minutes |
| Restart monitored process | `SaydinProcessRestarted` | 15 minutes |

Every critical test must reach the critical receiver and later send a resolve event.
Do not weaken TLS, role, secret, quota/limiter or ingestion-fence controls to create a
fault. Clean only exact staging resources after evidence is retained.
