# Public HTTPS or certificate expiry

Trigger: failed blackbox HTTPS probe or certificate expiry within 14 days.

1. Confirm DNS A/AAAA, Caddy process/storage, ACME account email and external reachability
   of ports 80/443. Internal API/DB/Redis/metrics ports must remain unpublished.
2. Inspect certificate issuer, SAN, validity and Caddy renewal logs without exposing ACME
   account material.
3. Correct DNS/firewall/time/storage causes; do not disable certificate verification or
   expose the API directly as a workaround.

Resolved when the public probe succeeds, HSTS is present and certificate lifetime is
more than 30 days.
