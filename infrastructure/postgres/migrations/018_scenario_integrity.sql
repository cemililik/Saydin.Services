-- REM-API-03 / API-05 / API-11: saved scenario integrity and atomic limits.
--
-- This migration is deliberately fail-closed. Existing rows are never deleted,
-- rewritten or normalized: incompatible data aborts the transaction before any
-- new constraint, index or trigger is installed.

DO $preflight$
BEGIN
    IF EXISTS (
        SELECT 1
          FROM saved_scenarios
         WHERE extra_data IS NOT NULL
           AND jsonb_typeof(extra_data) NOT IN ('object', 'null')
    ) THEN
        RAISE EXCEPTION 'saved_scenarios contains non-object extra_data'
            USING ERRCODE = '23514',
                  CONSTRAINT = 'chk_saved_scenarios_extra_data_object';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM saved_scenarios
         WHERE extra_data IS NOT NULL
           AND octet_length(extra_data::text) > 8192
    ) THEN
        RAISE EXCEPTION 'saved_scenarios contains extra_data above 8192 canonical UTF-8 bytes'
            USING ERRCODE = '23514',
                  CONSTRAINT = 'chk_saved_scenarios_extra_data_size';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM saved_scenarios
         WHERE type = 'dca' AND quantity_unit <> 'try'
    ) THEN
        RAISE EXCEPTION 'saved_scenarios contains a DCA row with a non-try quantity unit'
            USING ERRCODE = '23514',
                  CONSTRAINT = 'chk_saved_scenarios_type_unit';
    END IF;

    IF EXISTS (
        SELECT 1
          FROM saved_scenarios
         GROUP BY user_id
        HAVING count(*) > 100
    ) THEN
        RAISE EXCEPTION 'saved_scenarios contains a user above the system hard cap'
            USING ERRCODE = '23514',
                  CONSTRAINT = 'chk_saved_scenarios_hard_cap';
    END IF;
END
$preflight$;

ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_extra_data_object
    CHECK (extra_data IS NULL OR jsonb_typeof(extra_data) IN ('object', 'null'))
    NOT VALID;
ALTER TABLE saved_scenarios
    VALIDATE CONSTRAINT chk_saved_scenarios_extra_data_object;

ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_extra_data_size
    CHECK (extra_data IS NULL OR octet_length(extra_data::text) <= 8192)
    NOT VALID;
ALTER TABLE saved_scenarios
    VALIDATE CONSTRAINT chk_saved_scenarios_extra_data_size;

ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_type_unit
    CHECK (type <> 'dca' OR quantity_unit = 'try')
    NOT VALID;
ALTER TABLE saved_scenarios
    VALIDATE CONSTRAINT chk_saved_scenarios_type_unit;

CREATE INDEX idx_saved_scenarios_user_created_id_desc
    ON saved_scenarios (user_id, created_at DESC, id DESC);

-- The keyset index is a strict left-prefix superset of the original index.
-- Keeping both would duplicate every scenario write without serving a distinct
-- query contract.
DROP INDEX idx_saved_scenarios_user;

CREATE FUNCTION enforce_saved_scenario_hard_cap()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    scenario_count integer;
BEGIN
    -- The API repository uses this exact namespace and UUID text form before
    -- its configured-limit count. Direct writers therefore serialize with the
    -- same per-user lock instead of racing around the system hard cap.
    PERFORM pg_advisory_xact_lock(
        hashtextextended('saydin.saved_scenarios:' || NEW.user_id::text, 0));

    SELECT count(*)
      INTO scenario_count
      FROM public.saved_scenarios
     WHERE user_id = NEW.user_id;

    IF scenario_count >= 100 THEN
        RAISE EXCEPTION 'saved scenario system hard cap exceeded'
            USING ERRCODE = '23514',
                  CONSTRAINT = 'chk_saved_scenarios_hard_cap';
    END IF;

    RETURN NEW;
END
$function$;

REVOKE ALL ON FUNCTION enforce_saved_scenario_hard_cap() FROM PUBLIC;

CREATE TRIGGER trg_saved_scenarios_hard_cap
BEFORE INSERT ON saved_scenarios
FOR EACH ROW EXECUTE FUNCTION enforce_saved_scenario_hard_cap();

COMMENT ON CONSTRAINT chk_saved_scenarios_extra_data_size ON saved_scenarios IS
    'Maximum 8192 bytes measured from PostgreSQL jsonb canonical text.';
COMMENT ON FUNCTION enforce_saved_scenario_hard_cap() IS
    'Serializes all writers per user and rejects inserts after 100 saved scenarios.';
