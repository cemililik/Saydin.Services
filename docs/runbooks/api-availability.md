# API availability

Trigger: `SaydinApiUnavailable` or failed public HTTPS probe.

1. Compare public blackbox probe, Caddy health, API process health and dependency
   metrics. Do not restart while the cause is unknown.
2. Confirm the running API digest equals the signed release manifest and that the
   backend census contains the exact managed API login, never admin/superuser.
3. If Caddy alone is failing, inspect DNS, certificate and edge-network state. If API
   alone is failing, inspect stable startup/security codes and migration/DQA gates.
4. For DB or Redis faults, follow the corresponding runbook. Finite quota and security
   limiter intentionally return 503 when Redis is unavailable; do not fail-open.
5. For 503 admission storms, group
   `saydin_security_admission_decisions_total{outcome="unavailable"}` by `bucket,reason`.
   `client_address_untrusted` means the request still carries an unconsumed
   `X-Forwarded-For`, the observed client address is unusable, middleware ordering drifted,
   or the deployed `ForwardedHeaders__KnownNetworks`/`KnownProxies` no longer matches the
   Caddy network. Compare the exact deployed CIDR with the signed environment without
   widening it and without logging a client address.
6. If the release is causal and schema-compatible, redeploy the previous signed digest.

Resolved when the internal API scrape and public HTTPS probe are continuously healthy
for 15 minutes and a bounded authenticated smoke request succeeds.
