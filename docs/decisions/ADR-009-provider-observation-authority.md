# ADR-009 — Provider observation authority and immutable fetch attribution

- Status: Proposed / expand phase implemented
- Date: 2026-08-19
- Scope: price and CPI ingestion authority contract

## Context

A normalized daily observation is not the same thing as an HTTP fetch. The same provider
observation can arrive in a historical range response and a later daily response, with
different response-byte hashes and different ingestion windows. Storing one payload hash
or one window foreign key on `price_points` / `inflation_rates` would overwrite valid
forensic evidence and make orchestration order change the business row.

Provider contracts also differ materially:

- TCMB is an official reference buying rate and has no OHLC.
- Open Exchange Rates is a completed UTC daily reference and has no OHLC.
- CoinGecko must yield an exact 00:00 UTC daily point. Public/demo range granularity is
  automatic; the ingestion request therefore spans at least 91 days and rejects hourly
  points rather than selecting a nearest value.
- Twelve Data daily bars are exchange-local. BIST identity is pinned to `BIST`, MIC
  `XIST`, `Europe/Istanbul`, TRY and a supported stock type. The persisted instant is the
  exchange-local bar-open midnight converted to UTC; no fictional close second is used.
- EVDS CPI is a positive, month-first `TP.FG.J0` observation.

References: [CoinGecko range API](https://docs.coingecko.com/reference/coins-id-market-chart-range),
[CoinGecko granularity support note](https://support.coingecko.com/hc/en-us/articles/4538771776153),
[Twelve Data time series](https://twelvedata.com/docs), and
[Twelve Data XIST](https://twelvedata.com/exchanges/xist?group=regulatory).

## Decision

Migration 020 expands the schema and separates three authorities:

1. `price_points` and `inflation_rates` keep the normalized final observation: provider,
   stable observation ID, `as_of_at`, kind, contract version, allowlisted normalized
   evidence and a database-recomputed SHA-256. PostgreSQL canonicalizes the allowlisted
   scalar object and normalizes numeric scale before hashing, so `42`, `42.0` and
   `42.00` are the same observation while a real value change is not.
2. `provider_fetch_payloads` is an append-only ledger of provider plus raw HTTP response
   byte hash and bounded byte length. Raw HTTP bodies are not persisted.
3. Price/CPI attribution tables append the observation, ingestion window and fetch-payload
   relationship. Their uniqueness permits idempotent retry while preserving historical and
   daily envelopes independently.

The normalized business key is immutable at runtime. A replay with the same normalized
evidence preserves the original ingest timestamp while adding any new valid attribution.
Replacement and provisional writes are rejected. A future repair path requires a durable,
operator-authorized preimage-to-postimage record; an arbitrary UUID is not authorization.

All authority and attribution writes execute in the window-completion transaction. Database
triggers bind the presented live lease token and running, unexpired window to
asset/source/date/job/contract scope. Ledger timestamps are assigned by the database;
ingestion cannot supply or mutate them. Payload and attribution UPDATE, DELETE and
TRUNCATE operations are guarded. Ingestion receives only the minimum column-level
insert/update/select grants; audit receives read-only access; PUBLIC receives none.

HTTP adapters stream with `ResponseHeadersRead`, stop at 64 KiB, hash pre-parse bytes and
persist only bounded scalar evidence. OXR uses `Authorization: Token`; Twelve Data uses
`Authorization: apikey`; secrets never enter URLs or evidence. Enabled providers fail at
startup when their required secret is absent.

## Rollout and consequences

Migration 020 adds nullable columns and new tables. Legacy all-null authority rows remain
readable; partial new tuples are rejected. Historical classification, repair, constraint
validation and consumer final-only enforcement are separate contract phases.

This is not a concurrent old/new binary rollout. Before applying 020, operators must stop
and drain every authority-unaware ingestion worker; they then apply 020 and start only the
authority-aware binary. An old worker started after 020 fails closed on the required
authority trigger. The additive columns remain on rollback, but no old-binary fallback is
supported while 020's triggers are installed; a database rollback procedure would need to
be designed and exercised separately.

Migration 020 is intentionally not a production trust-root signal until the serialized
Migrator/DQA/CI phase pins its body/hash and updates the canonical fresh count to 22
(`001`–`020`, including `008b` and `012b`). Migrations `001`–`019` remain byte-frozen.

The CoinGecko 91-day response must remain within the 64 KiB cap. If the provider plan or
payload shape cannot satisfy both conditions, ingestion fails permanently and requires an
explicit product/capability decision; it does not silently loosen the evidence bound.
