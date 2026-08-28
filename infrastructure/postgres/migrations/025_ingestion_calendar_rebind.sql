-- Migration 025: audited recovery path for a permanently failed ingestion
-- window that is bound to a stale sealed calendar release.
--
-- DataRepair schema v2 performs one compare-and-swap transition from
-- permanent_failed to pending while clearing calendar_release_id. The next
-- normal ingestion claim must then resolve and bind the currently active,
-- sealed release before it can enter running state. All other changes to a
-- non-null calendar binding remain immutable.

BEGIN;

DO $contract_preflight$
DECLARE
    owner_role text;
BEGIN
    SELECT contract.owner_role
      INTO owner_role
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();

    IF owner_role IS NULL
       OR CURRENT_USER IS DISTINCT FROM owner_role
       OR pg_catalog.to_regprocedure(
              'public.enforce_ingestion_window_calendar_release()') IS NULL
       OR pg_catalog.pg_get_userbyid(
              (SELECT proowner FROM pg_catalog.pg_proc
                WHERE oid='public.enforce_ingestion_window_calendar_release()'
                          ::pg_catalog.regprocedure))
              IS DISTINCT FROM owner_role
       OR NOT EXISTS (
            SELECT 1
              FROM pg_catalog.pg_trigger trigger_row
             WHERE trigger_row.tgrelid='public.ingestion_windows'::pg_catalog.regclass
               AND trigger_row.tgname='trg_ingestion_window_calendar_release'
               AND NOT trigger_row.tgisinternal) THEN
        RAISE EXCEPTION 'ingestion calendar rebind preflight rejected'
            USING ERRCODE='42501';
    END IF;
END;
$contract_preflight$;

CREATE OR REPLACE FUNCTION public.enforce_ingestion_window_calendar_release()
RETURNS trigger LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    operator_rebind boolean :=
        TG_OP='UPDATE'
        AND OLD.calendar_release_id IS NOT NULL
        AND NEW.calendar_release_id IS NULL
        AND OLD.state='permanent_failed'
        AND NEW.state='pending'
        AND NEW.lease_owner IS NULL
        AND NEW.lease_token IS NULL
        AND NEW.lease_until IS NULL
        AND NEW.outcome_code IS NULL
        AND NEW.error_code IS NULL
        AND NEW.completed_at IS NULL;
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.calendar_release_id IS NOT NULL
       AND NEW.calendar_release_id IS DISTINCT FROM OLD.calendar_release_id
       AND NOT operator_rebind THEN
        RAISE EXCEPTION 'ingestion window calendar release is immutable: %', OLD.id
            USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.calendar_release_id IS NOT NULL
       AND (NEW.source, NEW.asset_id, NEW.job_type, NEW.range_start,
            NEW.range_end, NEW.contract_version)
           IS DISTINCT FROM
           (OLD.source, OLD.asset_id, OLD.job_type, OLD.range_start,
            OLD.range_end, OLD.contract_version) THEN
        RAISE EXCEPTION 'bound ingestion window logical key is immutable: %', OLD.id
            USING ERRCODE = '55000';
    END IF;
    IF NEW.contract_version >= 2 AND NEW.source IN ('tcmb','twelvedata')
       AND NEW.state <> 'pending' AND NEW.calendar_release_id IS NULL THEN
        RAISE EXCEPTION 'contract-v2 calendar release is required: %', NEW.id
            USING ERRCODE = '23514';
    END IF;
    IF NEW.calendar_release_id IS NOT NULL AND NOT EXISTS (
        SELECT 1
          FROM public.asset_market_calendars binding
          JOIN public.market_calendar_releases release
            ON release.calendar_code = binding.calendar_code
           AND release.id = NEW.calendar_release_id
         WHERE binding.asset_id = NEW.asset_id
           AND binding.source = NEW.source
           AND release.sealed_at IS NOT NULL) THEN
        RAISE EXCEPTION 'calendar release is unsealed or does not match window asset/source: %', NEW.id
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;

COMMENT ON FUNCTION public.enforce_ingestion_window_calendar_release() IS
'Keeps sealed calendar bindings immutable except for the exact permanent_failed-to-pending DataRepair rebind transition.';

COMMIT;
