# Alertmanager production material

`alertmanager.template.yml` is syntax-testable but deliberately undeployable: its two
receiver URLs are placeholders. The deployment control-plane renders the approved
critical/warning receivers into the external `alertmanager_secret` volume as
`private/alertmanager.yml`, owned by UID 65534 with directory mode 0700 and file mode
0400/0600. Receiver credentials must not enter Git, Compose environment, argv or logs.
The rendered file must also route `severity="watchdog"` to a repository-external
dead-man's-switch endpoint at a one-minute interval. The private-material preflight
rejects a missing watchdog route, placeholder URL, non-HTTPS URL, or a receiver that
does not send resolved notifications.

Promotion requires `amtool check-config` on the rendered file plus a synthetic firing
and resolved notification through the critical, warning and watchdog routes. The
external service must page when the heartbeat is absent for the operator-approved
window; keeping `SaydinWatchdog` firing is the healthy state.
