# Migration impact manifests

Every migration after the compiled trust-root prefix must have exactly two files in the configured impact directory:

- `<migration-version>.impact.json`: canonical UTF-8 JSON conforming to `impact-manifest.schema.json`.
- `<migration-version>.impact.sig`: one-line canonical Base64 of a P-256 ECDSA DER signature over the exact JSON bytes, using SHA-256.

The runner verifies the pinned public SPKI SHA-256, signature, migration SQL SHA-256, predecessor version/SHA, prefix-manifest SHA, target database, and cluster system-identifier SHA before connecting to the database. Extra or missing impact files fail closed.

Canonical JSON has no insignificant whitespace, sorts every object key by ordinal name, preserves array order, rejects duplicate object keys, and accepts only integers for JSON numbers. Arrays that represent classifications and relations must already be ordinally sorted. Generate canonical bytes in the release build process with the same contract as `CanonicalJson`; do not hand-edit a signed file.

The signing key is an offline release authority and must not be committed, mounted into the migrator, or supplied by environment/arguments. The migrator receives only a public-key file and its independently promoted SPKI SHA-256.

Configuration is all-or-none:

```text
--migration-impact-dir /run/release/migration-impact
--migration-impact-public-key-file /run/release/migration-impact-public.pem
--migration-impact-public-key-sha256 <64-lowercase-hex>
```

Equivalent environment variables are `SAYDIN_MIGRATION_IMPACT_DIR`, `SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_FILE`, and `SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_SHA256`.

The v1 online executor intentionally supports only `uuid-keyset-set-constant-where-null`. Adding another plan kind requires product code, static validation, a generated parameterized executor, real PostgreSQL/Timescale kill-resume tests, and a schema-version decision. A manifest can lower a limit but cannot raise the runner's compiled safety ceilings.
