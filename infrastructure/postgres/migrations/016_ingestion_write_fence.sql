-- Migration 016: fail-closed ingestion data-plane writer fence (C-01B)
--
-- Deployment order is deliberately strict:
--   1. stop/drain pre-016 ingestion replicas;
--   2. apply this migration;
--   3. start a binary that presents the claimed window capability with SET LOCAL.
--
-- Existing rows are untouched. After activation, INSERT/UPDATE without a live,
-- correctly-scoped ingestion-window lease fails closed at the database boundary.
-- The runtime role MUST NOT own these tables/functions, hold migration/DDL rights,
-- or be allowed to change session_replication_role. TimescaleDB cannot mark a
-- hypertable trigger ENABLE ALWAYS, so that least-privilege split is an explicit
-- rollout gate; the inflation table trigger is ENABLE ALWAYS.

BEGIN;

CREATE OR REPLACE FUNCTION public.enforce_price_point_ingestion_fence()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $fence$
DECLARE
    presented_window UUID;
    presented_token  UUID;
BEGIN
    BEGIN
        presented_window := NULLIF(
            pg_catalog.current_setting('saydin.ingestion_window_id', TRUE), '')::UUID;
        presented_token := NULLIF(
            pg_catalog.current_setting('saydin.ingestion_lease_token', TRUE), '')::UUID;
    EXCEPTION WHEN invalid_text_representation THEN
        RAISE EXCEPTION 'price_points write rejected: invalid ingestion capability'
            USING ERRCODE = '42501';
    END;

    IF presented_window IS NULL OR presented_token IS NULL OR NOT EXISTS (
        SELECT 1
          FROM public.ingestion_windows AS iw
          JOIN public.assets AS asset ON asset.id = NEW.asset_id
         WHERE iw.id = presented_window
           AND iw.state = 'running'
           AND iw.lease_token = presented_token
           AND iw.lease_until > pg_catalog.clock_timestamp()
           AND iw.asset_id = NEW.asset_id
           AND iw.source = asset.source
           AND iw.job_type IN ('historical_backfill', 'daily_update')
           AND NEW.price_date BETWEEN iw.range_start AND iw.range_end
    ) THEN
        RAISE EXCEPTION 'price_points write rejected: missing, expired, or out-of-scope ingestion lease'
            USING ERRCODE = '42501';
    END IF;

    RETURN NEW;
END;
$fence$;

CREATE OR REPLACE FUNCTION public.enforce_inflation_rate_ingestion_fence()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $fence$
DECLARE
    presented_window UUID;
    presented_token  UUID;
BEGIN
    -- Preserve the table's public source-domain contract even though this BEFORE
    -- trigger runs before PostgreSQL evaluates CHECK constraints.
    IF NEW.source NOT IN ('tuik', 'seed-approximation') THEN
        RAISE EXCEPTION 'new row for relation "inflation_rates" violates check constraint "chk_inflation_rates_source"'
            USING ERRCODE = '23514', CONSTRAINT = 'chk_inflation_rates_source';
    END IF;

    BEGIN
        presented_window := NULLIF(
            pg_catalog.current_setting('saydin.ingestion_window_id', TRUE), '')::UUID;
        presented_token := NULLIF(
            pg_catalog.current_setting('saydin.ingestion_lease_token', TRUE), '')::UUID;
    EXCEPTION WHEN invalid_text_representation THEN
        RAISE EXCEPTION 'inflation_rates write rejected: invalid ingestion capability'
            USING ERRCODE = '42501';
    END;

    IF presented_window IS NULL OR presented_token IS NULL OR NOT EXISTS (
        SELECT 1
          FROM public.ingestion_windows AS iw
         WHERE iw.id = presented_window
           AND iw.state = 'running'
           AND iw.lease_token = presented_token
           AND iw.lease_until > pg_catalog.clock_timestamp()
           AND iw.asset_id IS NULL
           AND iw.source = 'evds'
           AND iw.job_type IN ('inflation_backfill', 'inflation_daily')
           AND NEW.source = 'tuik'
           AND NEW.period_date BETWEEN iw.range_start AND iw.range_end
    ) THEN
        RAISE EXCEPTION 'inflation_rates write rejected: missing, expired, or out-of-scope ingestion lease'
            USING ERRCODE = '42501';
    END IF;

    RETURN NEW;
END;
$fence$;

DROP TRIGGER IF EXISTS trg_price_points_ingestion_fence ON public.price_points;
CREATE TRIGGER trg_price_points_ingestion_fence
BEFORE INSERT OR UPDATE ON public.price_points
FOR EACH ROW EXECUTE FUNCTION public.enforce_price_point_ingestion_fence();
-- TimescaleDB hypertables reject ALTER TABLE ... ENABLE ALWAYS TRIGGER. The
-- regular trigger is propagated to chunks and is fail-closed for the runtime
-- role; table-owner/superuser DDL and session_replication_role remain privileged
-- operations and must never be granted to the application role.

DROP TRIGGER IF EXISTS trg_inflation_rates_ingestion_fence ON public.inflation_rates;
CREATE TRIGGER trg_inflation_rates_ingestion_fence
BEFORE INSERT OR UPDATE ON public.inflation_rates
FOR EACH ROW EXECUTE FUNCTION public.enforce_inflation_rate_ingestion_fence();
ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_ingestion_fence;

COMMENT ON FUNCTION public.enforce_price_point_ingestion_fence() IS
    'Rejects price INSERT/UPDATE unless SET LOCAL presents a live, asset/source/job/date-scoped ingestion-window lease. Timescale hypertable trigger is regular-enabled.';
COMMENT ON FUNCTION public.enforce_inflation_rate_ingestion_fence() IS
    'Rejects inflation INSERT/UPDATE unless SET LOCAL presents a live EVDS/TUIK/job/month-scoped ingestion-window lease.';

COMMIT;
