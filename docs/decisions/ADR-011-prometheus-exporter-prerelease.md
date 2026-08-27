# ADR-011 — OpenTelemetry Prometheus exporter prerelease exception

- **Status:** Accepted, temporary exception
- **Date:** 2026-08-24
- **Decision owners:** Backend / platform
- **Related finding:** PR review Low 114

## Context

`OpenTelemetry.Exporter.Prometheus.AspNetCore` is the component that exposes the API's
management-port scrape endpoint. The official NuGet package is still prerelease-only;
its own package guidance says the component remains under development and recommends
OTLP for production. The repository pins `1.15.3-beta.1`, aligned with the stable
`OpenTelemetry` 1.15.3 core packages and their committed lock files.

A cosmetic move to a newer beta would not remove prerelease risk. Replacing the
endpoint immediately would change Prometheus target topology, metric translation,
alert admission and staging receipts that are already behaviorally verified.

## Options considered

1. **Keep an undocumented beta.** Rejected: dependency policy and upgrade ownership
   would remain implicit.
2. **Automatically take the newest beta.** Rejected: it creates uncontrolled breaking
   metric/name behavior without making the dependency stable.
3. **Replace direct scrape with OTLP → collector → Prometheus now.** Deferred: valid
   direction, but it requires a reviewed monitoring topology migration and complete
   alert/label equivalence evidence.
4. **Bounded prerelease exception.** Selected.

## Decision

The exact `1.15.3-beta.1` package remains digest/lock-file reproducible and is the only
approved prerelease dependency. The exception is admitted only while all of these
controls remain true:

- the scrape endpoint is management-port-only, not on the public product surface;
- production networking permits only the monitoring scrape plane to reach it;
- production admission verifies the exact target, metric names and required labels;
- package restore uses committed lock files and locked mode; and
- dependency review checks NuGet deprecation/vulnerability metadata and upstream
  release notes before any version change.

Review this exception on every OpenTelemetry dependency update and at least quarterly.
The review must explicitly choose one of: a stable exporter version, a separately
tested newer prerelease, or a topology migration to the stable OTLP exporter. A stable
compatible exporter release removes this exception; it is not deferred indefinitely.

## Consequences and exit evidence

The residual prerelease compatibility risk is explicit and confined to the private
scrape surface. Promotion remains blocked if scrape readiness, exact live target/metric
admission or alert tests fail. Exiting this ADR requires two equivalent staging runs
covering all alert rules and label contracts, followed by removal of the prerelease
package and this exception comment from `Directory.Packages.props`.

## References

- [Official NuGet package](https://www.nuget.org/packages/OpenTelemetry.Exporter.Prometheus.AspNetCore)
- [Observability architecture](../architecture/observability.md)
- [Observability game day](../runbooks/observability-game-day.md)
