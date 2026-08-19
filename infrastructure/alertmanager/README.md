# Alertmanager production material

`alertmanager.template.yml` is syntax-testable but deliberately undeployable: its two
receiver URLs are placeholders. The deployment control-plane renders the approved
critical/warning receivers into the external `alertmanager_secret` volume as
`private/alertmanager.yml`, owned by UID 65534 with directory mode 0700 and file mode
0400/0600. Receiver credentials must not enter Git, Compose environment, argv or logs.

Promotion requires `amtool check-config` on the rendered file plus a synthetic firing
and resolved notification through both routes.
