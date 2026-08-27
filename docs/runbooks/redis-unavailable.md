# Redis unavailable or memory pressure

Trigger: Redis unavailable or memory above 85 percent.

1. Preserve Redis exporter and host evidence; determine process, storage, memory or
   network cause.
2. Expect finite quota and security-limiter requests to fail closed with 503. Do not
   disable the limiter or convert finite quota to unlimited. Redis loss is a full public
   API outage: only public liveness and management endpoints are admission-exempt.
3. Split the cause with
   `saydin_security_admission_decisions_total{outcome="unavailable"}`. A
   `reason="redis_failure"` or `reason="malformed_reply"` series points to Redis/Lua;
   `reason="client_address_untrusted"` points to reverse-proxy trust drift, not Redis.
   Never add IP, network pseudonym, principal or Redis key labels while investigating.
4. Validate the mounted Redis config and exporter/API purpose-specific material without
   printing values. Confirm no password exists in inspect/argv/environment.
   `maxmemory-policy` must remain `noeviction`; never recover capacity by permitting
   eviction of quota or security-limiter keys.
5. If Redis is classified rebuildable, recreate only after confirming no authoritative
   state is stored there. Otherwise use the approved AOF/restore policy.

Resolved when Redis and exporter are healthy, two replicas share limiter/quota state,
and 503/429 contract smoke tests pass.
