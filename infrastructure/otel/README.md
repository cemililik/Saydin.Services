# OpenTelemetry production boundary

Production exports bounded metrics to the private Prometheus endpoint, traces to the
private Tempo service and logs to the private Loki service. None of these services has
a published host port. The Collector uses a durable external-volume queue, bounded
retry/failure telemetry and release/deployment resource attributes; Tempo and Loki use
private retention volumes and explicit 30-day retention.

The production validator rejects `nop`, missing trace/log backends, public telemetry
ports, ephemeral retention and a Collector outside the management network. Any future
external backend must add reviewed workload-identity or mTLS files rather than bearer
secrets in environment/argv, plus redaction and release-correlation acceptance.
