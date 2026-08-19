# Calendar acquisition and reviewed promotion

`run-acquisition.sh` is the only networked stage. It runs the calendar image with an
exact plan, one global `flock`, a 15 minute hard timeout and at most three attempts.
The CLI itself rejects non-HTTPS or non-official host/path/port redirects, unexpected
media/encoding, oversized content and parser drift. Its output remains under the
owner-only quarantine root; it never imports or activates a database release.

Install the template unit plus the two timers from `systemd/`. TCMB runs daily at
06:00 Europe/Istanbul. BIST runs annually on 15 October; the 45-day active-horizon
alert is the independent escalation if the official next-year PDF is not yet usable.
Materialize the `.plan.example.json` file with a new snapshot set id and reviewed
coverage before enabling a timer. A failed parse does not advance coverage.

After acquisition, a reviewer compares the raw official material and normalized diff,
then signs `review-envelope.json` outside the acquisition identity:

```text
openssl dgst -sha256 -sign REVIEWER_PRIVATE.pem \
  -out ENVELOPE.sig CANDIDATE/review-envelope.json
```

The private key must not be mounted into the acquisition or promotion runtime. The promotion
identity receives only the detached signature and pinned public key, then runs:

```text
promote-reviewed-bundle.sh CANDIDATE ENVELOPE.sig REVIEWER_PUBLIC.pem PROMOTION_ROOT RELEASE_NAME IMAGE_DIGEST
```

The script verifies the detached signature, envelope hashes and a Docker
`--network none` parser replay before an atomic owner-only publish. Promotion still
does not write PostgreSQL. Import/seal/activate remains the separately authorized
calendar-importer workflow documented in `tools/calendar-data/README.md`.
