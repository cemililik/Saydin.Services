-- Senaryo tipi ve tipe özgü ek veri desteği
-- Ayrıca asset sembol ve görünen adı doğrudan kolona taşıyoruz (join gerektirmez)

BEGIN;

-- 1. Yeni kolonlar ekle
ALTER TABLE saved_scenarios
    ADD COLUMN type               VARCHAR(20)  NOT NULL DEFAULT 'what_if',
    ADD COLUMN extra_data         JSONB,
    ADD COLUMN asset_symbol       VARCHAR(100),
    ADD COLUMN asset_display_name VARCHAR(200);

-- 2. Mevcut satırlar için asset tablosundan backfill yap
UPDATE saved_scenarios s
SET asset_symbol       = a.symbol,
    asset_display_name = a.display_name
FROM assets a
WHERE s.asset_id = a.id;

-- 3. Backfill sonrası NOT NULL yap
ALTER TABLE saved_scenarios
    ALTER COLUMN asset_symbol SET NOT NULL,
    ALTER COLUMN asset_display_name SET NOT NULL;

-- 4. asset_id artık nullable (comparison/portfolio tipinde NULL olabilir)
ALTER TABLE saved_scenarios
    ALTER COLUMN asset_id DROP NOT NULL;

-- 5. type değerleri için CHECK constraint
ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_type
    CHECK (type IN ('what_if', 'comparison', 'portfolio'));

COMMENT ON COLUMN saved_scenarios.type IS 'what_if | comparison | portfolio';
COMMENT ON COLUMN saved_scenarios.extra_data IS 'Tipe özgü ek veriler (JSON): karşılaştırmada kazanan, portföyde getiri vb.';
COMMENT ON COLUMN saved_scenarios.asset_symbol IS 'Doğrudan saklanan sembol (what_if=tek sembol, comparison=virgülle ayrılmış, portfolio=PORTFOLIO)';
COMMENT ON COLUMN saved_scenarios.asset_display_name IS 'Doğrudan saklanan görünen ad (join gerektirmez)';

COMMIT;
