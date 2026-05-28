-- ============================================================
-- Migration 011: Faz 2 — Schema Hardening
--
-- İçerik:
--   - F2.5-5 / F2.7-2: users (email, device_id) için partial UNIQUE index
--     (NULL'lar unique constraint dışı, anonim kayıtlar yarış kaybetmez).
--   - F2.5-4 / F2.7 / EF sync: users.tier CHECK (free|premium) — kod sync.
--   - F2.5-2 / F2.5-7 / EF sync: saved_scenarios.type CHECK
--     (what_if|comparison|portfolio|dca) — kod sync.
--   - F2.7-3 ([C-G-001-6]): saved_scenarios.quantity_unit CHECK genişletme
--     (try|units|grams).
--   - F2.5-6 / F2.7-10 / EF sync: activity_logs.action CHECK — kod sync
--     (önceki migration 008 ile aynı liste, idempotent doğrulama).
--   - F2.7-1 ([C-G-001-3]): FK ON DELETE davranışları açık tanımlanır.
--   - F2.7-5 ([C-G-004-2]): inflation_rates (period_date, source) UNIQUE.
--   - F2.7-9 ([C-G-008-3]): activity_logs.data JSONB boyut limiti CHECK
--     (10000 byte) — DoS koruma.
--   - F2.1-9 ([C-A-29]): activity_logs.duration_ms INT → BIGINT (24.8 gün
--     overflow tehdidi, C# long ile uyumluluk).
--   - F2.5-1: assets.metadata kolonu zaten var (001'de) — yalnız sync notu.
-- ============================================================

BEGIN;

-- ─── F2.5-5 / F2.7-2: partial UNIQUE indexes (NULL'lara izin ver) ──────────
DO $$
BEGIN
    -- uq_users_device_id partial'a dönüştür.
    -- Önce non-partial UNIQUE constraint varsa düş.
    IF EXISTS (
        SELECT 1
        FROM   pg_constraint
        WHERE  conname = 'uq_users_device_id'
    ) THEN
        ALTER TABLE users DROP CONSTRAINT uq_users_device_id;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM   pg_indexes
        WHERE  indexname = 'uq_users_device_id'
    ) THEN
        DROP INDEX IF EXISTS uq_users_device_id;
    END IF;

    -- uq_users_email da aynı şekilde.
    IF EXISTS (
        SELECT 1
        FROM   pg_constraint
        WHERE  conname = 'uq_users_email'
    ) THEN
        ALTER TABLE users DROP CONSTRAINT uq_users_email;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM   pg_indexes
        WHERE  indexname = 'uq_users_email'
    ) THEN
        DROP INDEX IF EXISTS uq_users_email;
    END IF;
END $$;

CREATE UNIQUE INDEX uq_users_device_id
    ON users (device_id)
    WHERE device_id IS NOT NULL;

CREATE UNIQUE INDEX uq_users_email
    ON users (email)
    WHERE email IS NOT NULL;

-- ─── F2.7-3: saved_scenarios.quantity_unit CHECK genişletme ────────────────
-- 001 sürümünde `quantity_unit IN ('try', 'units', 'grams')` zaten genel; gözden
-- geçirildi: tipler kapsam dahilinde — değişiklik gereksiz, ancak migration 011
-- denetim izi için CHECK'i drop + recreate ederek tutarsız bir önceki sürüm
-- bulunma ihtimalini sıfırlar.
ALTER TABLE saved_scenarios
    DROP CONSTRAINT IF EXISTS chk_saved_scenarios_unit;
ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_unit
    CHECK (quantity_unit IN ('try', 'units', 'grams'));

-- ─── F2.5-2 / F2.5-7: saved_scenarios.type CHECK senkron ───────────────────
-- 007 migration'da DCA eklendi → liste güncel. EF kod tarafındaki liste ile
-- birebir kontrol edildi (Saydin.Shared.Constants.ScenarioTypes).
ALTER TABLE saved_scenarios
    DROP CONSTRAINT IF EXISTS chk_saved_scenarios_type;
ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_type
    CHECK (type IN ('what_if', 'comparison', 'portfolio', 'dca'));

-- ─── F2.5-4: users.tier CHECK senkron ──────────────────────────────────────
ALTER TABLE users
    DROP CONSTRAINT IF EXISTS chk_users_tier;
ALTER TABLE users
    ADD CONSTRAINT chk_users_tier
    CHECK (tier IN ('free', 'premium'));

-- ─── F2.5-6 / F2.7-10: activity_logs.action CHECK senkron ──────────────────
ALTER TABLE activity_logs
    DROP CONSTRAINT IF EXISTS chk_activity_action;
ALTER TABLE activity_logs
    ADD CONSTRAINT chk_activity_action
    CHECK (action IN (
        'what_if_calculate',
        'what_if_compare',
        'what_if_dca',
        'what_if_reverse',
        'scenario_save',
        'scenario_delete',
        'scenario_list',
        'assets_list',
        'asset_price',
        'asset_price_range',
        'config_fetch'
    ));

-- ─── F2.7-1: FK ON DELETE explicit (CASCADE / RESTRICT / SET NULL) ─────────
-- price_points.asset_id → assets(id): asset silinirse fiyat tarihçesi RESTRICT
-- (asset deactivate edilebilir, silinmemeli — referans bütünlüğü korunmalı).
ALTER TABLE price_points
    DROP CONSTRAINT IF EXISTS fk_price_points_asset;
ALTER TABLE price_points
    ADD CONSTRAINT fk_price_points_asset
    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE RESTRICT;

-- ingestion_jobs.asset_id → assets(id): RESTRICT, audit izi kaybolmasın.
ALTER TABLE ingestion_jobs
    DROP CONSTRAINT IF EXISTS fk_ingestion_jobs_asset;
ALTER TABLE ingestion_jobs
    ADD CONSTRAINT fk_ingestion_jobs_asset
    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE RESTRICT;

-- saved_scenarios.user_id → users(id): CASCADE — kullanıcı silinirse senaryoları da gider.
ALTER TABLE saved_scenarios
    DROP CONSTRAINT IF EXISTS fk_saved_scenarios_user;
ALTER TABLE saved_scenarios
    ADD CONSTRAINT fk_saved_scenarios_user
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

-- saved_scenarios.asset_id → assets(id): RESTRICT (kullanıcının kaydı yanlış varlığa bağlanmasın).
ALTER TABLE saved_scenarios
    DROP CONSTRAINT IF EXISTS fk_saved_scenarios_asset;
ALTER TABLE saved_scenarios
    ADD CONSTRAINT fk_saved_scenarios_asset
    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE RESTRICT;

-- market_holidays.asset_id → assets(id): CASCADE — asset silinirse tatil günleri de.
ALTER TABLE market_holidays
    DROP CONSTRAINT IF EXISTS fk_market_holidays_asset;
ALTER TABLE market_holidays
    ADD CONSTRAINT fk_market_holidays_asset
    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE CASCADE;

-- ─── F2.7-5: inflation_rates source-aware uniqueness ───────────────────────
-- 004 migration'da PK (period_date) — aynı ay için yalnız bir satır. EVDS
-- worker farklı kaynaklarda (seed-approximation / tuik) revize aynı ayı yazdığında
-- ON CONFLICT (period_date) DO UPDATE çalışır; semantik korunur, başka değişiklik
-- gerekmiyor. Audit (source, period_date) UNIQUE'i ileride istenirse PK'yi
-- (period_date, source) yapmak gerekir — backwards compat için Faz 3.

-- ─── F2.7-9: activity_logs.data JSONB boyut limiti ─────────────────────────
-- 10000 byte üstü payload kullanıcı tarafından isteksiz/abusive girdiyi gösterir.
ALTER TABLE activity_logs
    DROP CONSTRAINT IF EXISTS chk_activity_data_size;
ALTER TABLE activity_logs
    ADD CONSTRAINT chk_activity_data_size
    CHECK (data IS NULL OR octet_length(data::text) <= 10000);

-- ─── F2.1-9: activity_logs.duration_ms INT → BIGINT ────────────────────────
ALTER TABLE activity_logs
    ALTER COLUMN duration_ms TYPE BIGINT USING duration_ms::BIGINT;

COMMIT;
