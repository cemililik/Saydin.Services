# Unexpected runtime restart

Trigger: monitored `process_start_time_seconds` changed within 15 minutes.

1. Record service, deployment ID, release digest, exit code/OOM state and restart count.
2. Check bounded logs, resource limits, read-only/tmpfs failures, secret preflight and
   managed DB identity probe. Never echo mounted material.
3. For API shutdown, verify activity writer drain completed within stop grace. For
   ingestion, verify no running window/lease was left inconsistent.
4. Repeated restart is a release blocker. Roll back only to a signed schema-compatible
   digest; do not remove hardening or increase limits without evidence.

Resolved after 30 minutes without another restart and service-specific smoke/DQA gates
pass.
