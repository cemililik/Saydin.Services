# Calendar acquisition and reviewed promotion

`run-acquisition.sh` is the only networked stage. It runs the calendar image with an
exact plan, one global `flock`, a 15 minute hard timeout and at most three attempts.
The CLI itself rejects non-HTTPS or non-official host/path/port redirects, unexpected
media/encoding, oversized content and parser drift. Its output remains under the
owner-only quarantine root; it never imports or activates a database release.

Install a dedicated `saydin-calendar` host account with uid/gid `1001`, grant that account
only the host's Docker socket group required by this one-shot unit, then install the
template unit plus the two timers from `systemd/`. TCMB runs daily at 06:00
Europe/Istanbul. Its plan is deterministically materialized on every invocation; a
second invocation reuses the byte-identical plan/candidate or reports that the active
base already carries the snapshot set. Requested coverage is clamped to the latest
publication present in the refreshed official archive, so weekends, holidays and a
not-yet-published current day cannot advance coverage. BIST runs annually on 15 October;
the 45-day active-horizon alert is the independent escalation if the official next-year
PDF is not yet usable.

After acquisition, a reviewer compares the raw official material and normalized diff,
then signs `review-envelope.json` outside the acquisition identity:

```text
openssl dgst -sha256 -sign REVIEWER_PRIVATE.pem \
  -out ENVELOPE.sig CANDIDATE/review-envelope.json
```

The private key must not be mounted into the acquisition or promotion runtime. The promotion
identity receives only the detached signature and pinned public key. The promotion command runs
under the same dedicated uid/gid 1001 filesystem identity (authorization and reviewer keys remain
separate) so owner-only candidate and pending bytes are readable without root, then runs:

```text
promote-reviewed-bundle.sh CANDIDATE ENVELOPE.sig REVIEWER_PUBLIC.pem PROMOTION_ROOT RELEASE_NAME IMAGE_DIGEST
```

`SAYDIN_CALENDAR_REVIEWER_PUBLIC_KEY_SHA256` pins the exact reviewer public-key bytes and
`SAYDIN_CALENDAR_VERIFY_CANDIDATE` pins the absolute, regular verifier executable. Both are
required. The verifier replays as the candidate directory's uid/gid, matching the image's
non-root uid 1001 for the documented acquisition/promotion flow. A successful promotion
removes only the exact verified quarantine candidate; failed `.pending-*` acquisition and
promotion directories are cleaned before returning.

The script verifies the detached signature, envelope hashes and a Docker
`--network none` parser replay before an atomic owner-only publish. Promotion still
does not write PostgreSQL. Import/seal/activate remains the separately authorized
calendar-importer workflow documented in `tools/calendar-data/README.md`.

The CLI is an intentionally small one-shot control-plane executable rather than a hosted
service. Its composition root is the documented exception to the service-wide
`IHttpClientFactory`, `ILogger` and `Console.WriteLine` conventions: it constructs one hardened
`SocketsHttpHandler`, emits bounded machine-readable stdout/stderr contracts consumed by the
operator scripts, and maps typed failures to stable process exit codes. No secret or raw
provider payload is written to those streams.
