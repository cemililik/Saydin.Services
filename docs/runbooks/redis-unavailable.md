# Redis unavailable or memory pressure

Trigger: Redis unavailable or memory above 85 percent.

1. Preserve Redis exporter and host evidence; determine process, storage, memory or
   network cause.
2. Expect finite quota and security-limiter requests to fail closed with 503. Do not
   disable the limiter or convert finite quota to unlimited.
3. Validate the mounted Redis config and exporter/API purpose-specific material without
   printing values. Confirm no password exists in inspect/argv/environment.
   `maxmemory-policy` must remain `noeviction`; never recover capacity by permitting
   eviction of quota or security-limiter keys.
4. If Redis is classified rebuildable, recreate only after confirming no authoritative
   state is stored there. Otherwise use the approved AOF/restore policy.

Resolved when Redis and exporter are healthy, two replicas share limiter/quota state,
and 503/429 contract smoke tests pass.
