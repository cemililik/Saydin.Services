-- ============================================================
-- Migration 011: Faz 2 — Schema Hardening
--
-- İçerik (Faz 2 ACTION-PLAN.md ref):
--   - F2.5-5 / F2.7-2 : users (email, device_id) için partial UNIQUE index
--   - F2.5-4 / EF sync: users.tier CHECK (free|premium)
--   - F2.5-2 / F2.5-7 / EF sync: saved_scenarios.type CHECK
--   - F2.7-3 ([C-G-001-6]): saved_scenarios.quantity_unit CHECK
--   - F2.5-6 / F2.7-10 / EF sync: activity_logs.action CHECK
--   - F2.7-1 ([C-G-001-3]): FK ON DELETE açık tanımlanır
--   - F2.7-9 ([C-G-008-3]): activity_logs.data JSONB boyut limiti CHECK
--   - F2.1-9 ([C-A-29]): activity_logs.duration_ms INT → BIGINT
--   - F2.5-1: assets.metadata kolonu zaten 001'de var
--
-- Faz 2 review (P2R) follow-up'ı:
--   - INFR-001: CHECK constraint'ler NOT VALID + VALIDATE pattern (mevcut
--     aykırı satır migration'ı bloklamasın; audit eksikse fail-fast verir).
--   - INFR-008: CREATE UNIQUE INDEX IF NOT EXISTS — kısmen uygulanmış
--     migration rerun fail etmez.
--   - INFR-009: octet_length(data::text) → pg_column_size(data) — TOAST-
--     uncompressed binary boyutu, JSONB serialize CPU'su kalkar.
--   - INFR-010 / INFR-002: maintenance window runbook'u (aşağıda).
--
-- ============================================================
--  ÜRETİM DEPLOY RUNBOOK
-- ============================================================
-- Bu migration sayfası **maintenance window** içinde uygulanır. Lock alacak
-- DDL'ler vardır; küçük tabloda saniyeler, büyük tabloda dakikalar sürer.
--
--   1. Pre-flight audit (transaction dışında, read-only):
--        \i 011_audit.sql   -- (PHASE-2-DOC-UPDATE-NOTES §9'daki query'ler)
--      Aykırı satır varsa migration'ı durdur, önce data clean-up yap.
--
--   2. activity_logs hypertable BIGINT cast ön hazırlığı (INFR-002):
--      TimescaleDB hypertable + 7 gün üstü chunk'lar compress edilmiş olabilir;
--      bu durumda ALTER COLUMN TYPE BIGINT "cannot change column type of
--      compressed chunk" hatası verir. Production deploy adımları (sırasıyla,
--      psql konsolunda manuel çalıştırılır; sed S125 false-positive yapmasın
--      diye düz metin olarak açıklanır, indented SQL bloğu kullanılmaz):
--      compression policy kaldırılır; tüm chunk'lar decompress edilir; bu
--      migration uygulanır; sonra compression policy yeniden eklenir.
--      Komutlar PHASE-2-DOC-UPDATE-NOTES §9 deploy runbook'unda da listelenir.
--      Lokal dev'de chunk'lar tipik olarak compress edilmemiş; doğrudan ALTER çalışır.
--
--   3. INDEX yaratımı non-CONCURRENTLY → users üzerinde ShareLock (INFR-010).
--      Küçük tabloda (<1M satır) saniyeler sürer; büyük tabloda CONCURRENTLY
--      versiyonu için 011 ikiye bölünmeli (transaction dışında CONCURRENTLY).
--
--   4. CHECK constraint'ler NOT VALID + VALIDATE (INFR-001).
--      NOT VALID: yeni satırlar kontrol edilir; mevcut satır taranmaz → DDL
--      hızlı. VALIDATE: ayrı statement, ShareUpdateExclusiveLock ile mevcut
--      satırlar taranır. Bu migration'da ikisi de aynı transaction içinde —
--      mevcut data aykırıysa VALIDATE fail ve tüm migration rollback. Audit
--      adımı (1) bu nedenle kritik.
--
-- ============================================================

BEGIN;

-- ─── F2.5-5 / F2.7-2: partial UNIQUE indexes (NULL'lara izin ver) ──────────
-- INFR-008: IF NOT EXISTS ile rerun safe.

-- Önce non-partial UNIQUE constraint/index'i drop et (idempotent).
DO $$
BEGIN
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
        WHERE  schemaname = 'public'
        AND    indexname = 'uq_users_device_id'
        AND    indexdef NOT LIKE '%WHERE (device_id IS NOT NULL)%'
    ) THEN
        -- Sadece non-partial versiyonu drop et — partial zaten doğru durumda.
        DROP INDEX IF EXISTS uq_users_device_id;
    END IF;

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
        WHERE  schemaname = 'public'
        AND    indexname = 'uq_users_email'
        AND    indexdef NOT LIKE '%WHERE (email IS NOT NULL)%'
    ) THEN
        DROP INDEX IF EXISTS uq_users_email;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS uq_users_device_id
    ON users (device_id)
    WHERE device_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_users_email
    ON users (email)
    WHERE email IS NOT NULL;

-- ─── F2.7-3: saved_scenarios.quantity_unit CHECK ───────────────────────────
-- INFR-001: NOT VALID + VALIDATE (aykırı satır varsa VALIDATE fail eder ve
-- migration rollback; audit aşaması Bölüm 1'de zorunlu).
ALTER TABLE saved_scenarios
    DROP CONSTRAINT IF EXISTS chk_saved_scenarios_unit;
ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_unit
    CHECK (quantity_unit IN ('try', 'units', 'grams')) NOT VALID;
ALTER TABLE saved_scenarios
    VALIDATE CONSTRAINT chk_saved_scenarios_unit;

-- ─── F2.5-2 / F2.5-7: saved_scenarios.type CHECK senkron ───────────────────
ALTER TABLE saved_scenarios
    DROP CONSTRAINT IF EXISTS chk_saved_scenarios_type;
ALTER TABLE saved_scenarios
    ADD CONSTRAINT chk_saved_scenarios_type
    CHECK (type IN ('what_if', 'comparison', 'portfolio', 'dca')) NOT VALID;
ALTER TABLE saved_scenarios
    VALIDATE CONSTRAINT chk_saved_scenarios_type;

-- ─── F2.5-4: users.tier CHECK senkron ──────────────────────────────────────
ALTER TABLE users
    DROP CONSTRAINT IF EXISTS chk_users_tier;
ALTER TABLE users
    ADD CONSTRAINT chk_users_tier
    CHECK (tier IN ('free', 'premium')) NOT VALID;
ALTER TABLE users
    VALIDATE CONSTRAINT chk_users_tier;

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
    )) NOT VALID;
ALTER TABLE activity_logs
    VALIDATE CONSTRAINT chk_activity_action;

-- ─── SHRD-001 / SHRD-002 follow-up: ingestion_jobs CHECK'leri EF tarafında ──
-- 001'de tanımlı constraint'ler EF model'inde yoktu → Add-Migration "drop"
-- üretebiliyordu. Idempotent olarak yeniden ekle, EF Configuration ile sync.
ALTER TABLE ingestion_jobs
    DROP CONSTRAINT IF EXISTS chk_ingestion_jobs_type;
ALTER TABLE ingestion_jobs
    ADD CONSTRAINT chk_ingestion_jobs_type
    CHECK (job_type IN ('historical_backfill', 'daily_update', 'inflation_backfill', 'inflation_daily')) NOT VALID;
ALTER TABLE ingestion_jobs
    VALIDATE CONSTRAINT chk_ingestion_jobs_type;

ALTER TABLE ingestion_jobs
    DROP CONSTRAINT IF EXISTS chk_ingestion_jobs_status;
ALTER TABLE ingestion_jobs
    ADD CONSTRAINT chk_ingestion_jobs_status
    CHECK (status IN ('running', 'success', 'failed')) NOT VALID;
ALTER TABLE ingestion_jobs
    VALIDATE CONSTRAINT chk_ingestion_jobs_status;

-- ─── SHRD-011: inflation_rates.source CHECK ────────────────────────────────
ALTER TABLE inflation_rates
    DROP CONSTRAINT IF EXISTS chk_inflation_rates_source;
ALTER TABLE inflation_rates
    ADD CONSTRAINT chk_inflation_rates_source
    CHECK (source IN ('tuik', 'seed-approximation')) NOT VALID;
ALTER TABLE inflation_rates
    VALIDATE CONSTRAINT chk_inflation_rates_source;

-- ─── F2.7-1: FK ON DELETE explicit (CASCADE / RESTRICT / SET NULL) ─────────
-- F3 follow-up: PostgreSQL ≥ 9.2 FOREIGN KEY için NOT VALID + VALIDATE pattern
-- destekler. NOT VALID: yeni satır FK kontrol eder; mevcut satırlar taranmaz →
-- ADD CONSTRAINT hızlı ve ShareRowExclusiveLock kısa sürer. VALIDATE: ayrı
-- statement, ShareUpdateExclusiveLock (concurrent yazma engellenmez) ile mevcut
-- satırlar taranır. Bu migration'da ikisi de aynı transaction içinde —
-- mevcut data aykırıysa VALIDATE fail ve tüm migration rollback. Audit adımı
-- (üst runbook §1) bu nedenle kritik.
ALTER TABLE price_points
    DROP CONSTRAINT IF EXISTS fk_price_points_asset;
ALTER TABLE price_points
    ADD CONSTRAINT fk_price_points_asset
    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE RESTRICT NOT VALID;
ALTER TABLE price_points
    VALIDATE CONSTRAINT fk_price_points_asset;

ALTER TABLE ingestion_jobs
    DROP CONSTRAINT IF EXISTS fk_ingestion_jobs_asset;
ALTER TABLE ingestion_jobs
    ADD CONSTRAINT fk_ingestion_jobs_asset
    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE RESTRICT NOT VALID;
ALTER TABLE ingestion_jobs
    VALIDATE CONSTRAINT fk_ingestion_jobs_asset;

ALTER TABLE saved_scenarios
    DROP CONSTRAINT IF EXISTS fk_saved_scenarios_user;
ALTER TABLE saved_scenarios
    ADD CONSTRAINT fk_saved_scenarios_user
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE NOT VALID;
ALTER TABLE saved_scenarios
    VALIDATE CONSTRAINT fk_saved_scenarios_user;

ALTER TABLE saved_scenarios
    DROP CONSTRAINT IF EXISTS fk_saved_scenarios_asset;
ALTER TABLE saved_scenarios
    ADD CONSTRAINT fk_saved_scenarios_asset
    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE RESTRICT NOT VALID;
ALTER TABLE saved_scenarios
    VALIDATE CONSTRAINT fk_saved_scenarios_asset;

ALTER TABLE market_holidays
    DROP CONSTRAINT IF EXISTS fk_market_holidays_asset;
ALTER TABLE market_holidays
    ADD CONSTRAINT fk_market_holidays_asset
    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE CASCADE NOT VALID;
ALTER TABLE market_holidays
    VALIDATE CONSTRAINT fk_market_holidays_asset;

-- ─── F2.7-9 / INFR-009: activity_logs.data JSONB boyut limiti ──────────────
-- pg_column_size — TOAST-uncompressed binary boyutu döner. octet_length(::text)
-- her INSERT'te JSONB → text serialize + UTF-8 byte sayımı (hot path CPU).
-- 10000 binary byte limit data class'ı için makul.
ALTER TABLE activity_logs
    DROP CONSTRAINT IF EXISTS chk_activity_data_size;
ALTER TABLE activity_logs
    ADD CONSTRAINT chk_activity_data_size
    CHECK (data IS NULL OR pg_column_size(data) <= 10000) NOT VALID;
ALTER TABLE activity_logs
    VALIDATE CONSTRAINT chk_activity_data_size;

-- ─── F2.1-9 / INFR-002: activity_logs.duration_ms INT → BIGINT ─────────────
-- HYPERTABLE NOTU: Bu ALTER COLUMN compressed chunk varsa fail eder.
-- Bkz. runbook §2 (üst yorum) — production'da compression policy'yi geçici
-- olarak kaldır, chunk'ları decompress et, ALTER'i çalıştır, policy'yi geri ekle.
-- Lokal dev'de chunk'lar henüz compress edilmemiş olduğu için doğrudan çalışır.
ALTER TABLE activity_logs
    ALTER COLUMN duration_ms TYPE BIGINT USING duration_ms::BIGINT;

-- ─── SHRD-013: index adı düzelt (kolon adıyla yansıtsın) ────────────────────
-- idx_activity_logs_asset_symbol yanıltıcı (data JSONB üzerinde GIN).
ALTER INDEX IF EXISTS idx_activity_logs_asset_symbol
    RENAME TO idx_activity_logs_data_gin;

COMMIT;
