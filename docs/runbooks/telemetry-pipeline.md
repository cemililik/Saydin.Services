# Telemetry pipeline degradation

This alert covers exporter send/enqueue failures and sustained durable-queue pressure.
Tempo and Loki are private, single-site forensic backends; they are not an off-site archive.

1. Confirm `otel-collector`, `tempo`, and `loki` health/restart state on the management network.
2. Check `otelcol_exporter_queue_size`, `otelcol_exporter_queue_capacity`, enqueue failures,
   and send failures by exporter. Do not attach raw log bodies, IP addresses, installation IDs,
   or credentials to the incident ticket.
3. Check free space and ownership on the external `otel_queue`, `tempo_data`, and `loki_data`
   volumes. Never delete queue/storage data while the services are running.
4. If a backend is unavailable, restore it before restarting the Collector so the disk-backed
   queue can drain. A growing queue with a healthy backend is an escalation to the platform owner.
5. Confirm traces and logs carry the release SHA, release version, deployment ID, environment,
   and service namespace after recovery.
6. Resolve only after export failures remain zero and queue utilization stays below 50% for
   30 minutes. Record any forensic gap bounded by the alert start/end timestamps.
