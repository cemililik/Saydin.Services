-- Migration 015: durable ingestion-window ledger (C-01 / ING-001)
-- Additive expand only: existing price/inflation/job readers remain compatible.
-- The runner owns the outer transaction; this file intentionally includes the
-- historical BEGIN/COMMIT wrapper so normalizer coverage remains exercised.

BEGIN;

CREATE TABLE ingestion_windows (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    source              VARCHAR(30) NOT NULL,
    asset_id            UUID        NULL,
    job_type            VARCHAR(50) NOT NULL,
    range_start         DATE        NOT NULL,
    range_end           DATE        NOT NULL,
    contract_version    INTEGER     NOT NULL,
    state               VARCHAR(30) NOT NULL DEFAULT 'pending',
    lease_owner         VARCHAR(120) NULL,
    lease_token         UUID        NULL,
    lease_until         TIMESTAMPTZ NULL,
    attempt_count       INTEGER     NOT NULL DEFAULT 0,
    next_attempt_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    requested_calendar_count   INTEGER NOT NULL DEFAULT 0,
    expected_observation_count INTEGER NOT NULL DEFAULT 0,
    raw_item_count              INTEGER NOT NULL DEFAULT 0,
    accepted_distinct_count     INTEGER NOT NULL DEFAULT 0,
    rejected_count              INTEGER NOT NULL DEFAULT 0,
    expected_no_data_count      INTEGER NOT NULL DEFAULT 0,
    outcome_code        VARCHAR(80) NULL,
    error_code          VARCHAR(80) NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at        TIMESTAMPTZ NULL,

    CONSTRAINT fk_ingestion_windows_asset
        FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE RESTRICT,
    CONSTRAINT uq_ingestion_windows_logical
        UNIQUE NULLS NOT DISTINCT
        (source, asset_id, job_type, range_start, range_end, contract_version),
    CONSTRAINT chk_ingestion_windows_range CHECK (range_start <= range_end),
    CONSTRAINT chk_ingestion_windows_contract CHECK (contract_version > 0),
    CONSTRAINT chk_ingestion_windows_attempt CHECK (attempt_count >= 0),
    CONSTRAINT chk_ingestion_windows_counts CHECK (
        requested_calendar_count >= 0 AND expected_observation_count >= 0
        AND raw_item_count >= 0 AND accepted_distinct_count >= 0
        AND rejected_count >= 0 AND expected_no_data_count >= 0
        AND expected_observation_count <= requested_calendar_count
        AND accepted_distinct_count <= raw_item_count),
    CONSTRAINT chk_ingestion_windows_terminal_completeness CHECK (
        (state = 'succeeded' AND accepted_distinct_count > 0
            AND rejected_count = 0
            AND accepted_distinct_count = expected_observation_count
            AND expected_no_data_count = requested_calendar_count - expected_observation_count)
        OR (state = 'expected_no_data' AND requested_calendar_count > 0
            AND expected_observation_count = 0 AND accepted_distinct_count = 0
            AND rejected_count = 0
            AND expected_no_data_count = requested_calendar_count)
        OR state NOT IN ('succeeded', 'expected_no_data')),
    CONSTRAINT chk_ingestion_windows_state CHECK (state IN (
        'pending', 'running', 'succeeded', 'expected_no_data',
        'retryable_failed', 'permanent_failed', 'cancelled', 'abandoned')),
    CONSTRAINT chk_ingestion_windows_lease CHECK (
        (state = 'running' AND lease_owner IS NOT NULL AND lease_token IS NOT NULL AND lease_until IS NOT NULL)
        OR (state <> 'running' AND lease_owner IS NULL AND lease_token IS NULL AND lease_until IS NULL)),
    CONSTRAINT chk_ingestion_windows_completed CHECK (
        (state IN ('succeeded', 'expected_no_data', 'permanent_failed') AND completed_at IS NOT NULL)
        OR (state NOT IN ('succeeded', 'expected_no_data', 'permanent_failed') AND completed_at IS NULL)),
    CONSTRAINT chk_ingestion_windows_outcome_codes CHECK (
        (state IN ('pending', 'running') AND outcome_code IS NULL)
        OR (state IN ('succeeded', 'expected_no_data', 'retryable_failed', 'permanent_failed', 'cancelled', 'abandoned')
            AND outcome_code IS NOT NULL)),
    CONSTRAINT chk_ingestion_windows_error_codes CHECK (
        (state IN ('retryable_failed', 'permanent_failed') AND error_code IS NOT NULL)
        OR (state NOT IN ('retryable_failed', 'permanent_failed') AND error_code IS NULL))
);

CREATE INDEX idx_ingestion_windows_claim
    ON ingestion_windows
        (source, asset_id, job_type, contract_version, range_start, range_end)
    WHERE state NOT IN ('succeeded', 'expected_no_data');

CREATE INDEX idx_ingestion_windows_lease_expiry
    ON ingestion_windows (lease_until)
    WHERE state = 'running';

ALTER TABLE ingestion_jobs
    ADD COLUMN window_id UUID NULL,
    ADD COLUMN outcome_code VARCHAR(80) NULL;

ALTER TABLE ingestion_jobs
    ADD CONSTRAINT fk_ingestion_jobs_window
    FOREIGN KEY (window_id) REFERENCES ingestion_windows(id) ON DELETE RESTRICT;

CREATE INDEX idx_ingestion_jobs_window_started
    ON ingestion_jobs (window_id, started_at DESC)
    WHERE window_id IS NOT NULL;

COMMENT ON TABLE ingestion_windows IS
    'Durable logical ingestion ranges, claim leases, retry state and completeness counters (C-01/ING-001).';
COMMENT ON COLUMN ingestion_windows.contract_version IS
    'Provider mapping/completeness contract version; part of the immutable logical key.';
COMMENT ON COLUMN ingestion_jobs.window_id IS
    'Nullable durable ingestion window correlation; null for pre-015 and legacy writers.';
COMMENT ON COLUMN ingestion_jobs.outcome_code IS
    'Stable machine-readable terminal outcome; nullable for pre-015 and legacy writers.';

COMMIT;
