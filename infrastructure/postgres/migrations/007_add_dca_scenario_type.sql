-- DCA senaryo tipini CHECK constraint'e ekle

BEGIN;

ALTER TABLE saved_scenarios
    DROP CONSTRAINT chk_saved_scenarios_type;

ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_type
    CHECK (type IN ('what_if', 'comparison', 'portfolio', 'dca'));

COMMENT ON COLUMN saved_scenarios.type IS 'what_if | comparison | portfolio | dca';

COMMIT;
