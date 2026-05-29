-- ============================================================
-- Migration 012: Faz 3 — Schema (inflation source, ingestion job audit)
--
-- İçerik (Faz 3 ACTION-PLAN.md ref):
--   - F2.7-5 ([C-G-004-2]): inflation_rates composite PK (period_date, source)
--       → aynı ay için hem 'seed-approximation' hem gerçek 'tuik' satırı bir
--         arada tutulabilir (audit trail). Okuma yolu 'tuik'i tercih eder
--         (Saydin.Api InflationRepository.GetIndexValuesAsync).
--   - INGR-002: ingestion_jobs EVDS (inflation) job kayıtlarını destekler
--       → asset_id NULLABLE (inflation bir asset değil, aylık endeks serisidir)
--       → source kolonu (hangi dış kaynak: 'evds', 'tcmb', 'coingecko' ...)
--       (job_type CHECK zaten 011'de 'inflation_backfill'|'inflation_daily' içeriyor.)
--   - INFR-005: postgres-exporter least-privilege rol (`saydin_exporter` + pg_monitor)
--       → 012b_create_exporter_role.sh ile (parola env'den; SQL dosyasına sır yazılmaz).
--   - F2.7-4 ([C-G-003-2]): Eski GoldAPI fiyat verisi temizliği — aşağıda açıklama.
--
-- ============================================================
--  F2.7-4 — GoldAPI FİYAT VERİSİ TEMİZLİĞİ: BİLİNÇLİ NO-OP
-- ============================================================
-- Bu kodtabanında GoldAPI ingestion path'i YOKTUR (aktif adapter yok). XAU/XAG
-- asset'lerinin source'u migration 003 ile 'goldapi' → 'openexchangerates' olarak
-- güncellendi. `price_points` tablosunda **source ayırt edici kolon yoktur** (yalnız
-- source_raw JSONB) ve satırlar yalnızca aktif adapter'lar tarafından
-- ON CONFLICT (asset_id, price_date) DO UPDATE ile yazılır — yani OXR verisi eski
-- GoldAPI değerlerini aynı (asset_id, price_date) için doğal olarak ÜZERİNE YAZAR.
--
-- Sonuç: Seçici ("yalnızca GoldAPI") DELETE güvenli biçimde ifade EDİLEMEZ;
-- XAU/XAG geçmişini topluca silmek geçerli OXR verisini de yok eder. Bu nedenle
-- yıkıcı bir DELETE eklenmemiştir. Provenance gerektiğinde ileride price_points'e
-- `source` kolonu eklenmesi ayrı bir migration ile değerlendirilir (Faz 4 backlog).
-- (Karar denetim izi için ACTION-PLAN F2.7-4 ve PHASE-3-DOC-UPDATE-NOTES'ta belgelendi.)
--
-- ============================================================
--  ÜRETİM DEPLOY RUNBOOK
-- ============================================================
--  1. Pre-flight audit (transaction dışında, read-only):
--       -- Composite PK öncesi olası çakışma (eski tek-kolon PK ile imkânsız ama doğrula):
--       SELECT period_date, source, COUNT(*) FROM inflation_rates
--         GROUP BY period_date, source HAVING COUNT(*) > 1;
--       -- ingestion_jobs NULL asset_id'ye geçişten etkilenecek raporlama view'ları taranmalı.
--  2. saydin_exporter rolü: fresh init'te 012b_create_exporter_role.sh otomatik yaratır
--       (POSTGRES_EXPORTER_PASSWORD set ise). Mevcut DB'de manuel:
--         CREATE ROLE saydin_exporter LOGIN PASSWORD :'pw'; GRANT pg_monitor TO saydin_exporter;
--  3. Bu migration küçük tablo (inflation_rates ~birkaç yüz satır) varsayar; PK
--     yeniden oluşturma saniyeler sürer. ingestion_jobs ALTER COLUMN metadata-only
--     (DROP NOT NULL) → kısa ACCESS EXCLUSIVE lock.
-- ============================================================

BEGIN;

-- ─── F2.7-5: inflation_rates composite PK (period_date, source) ─────────────
-- Eski PK yalnız period_date idi → ay başına tek satır; gerçek TÜİK verisi seed'i
-- ezerdi. Composite PK ile seed + tuik bir arada (audit). source NOT NULL (004) ve
-- period_date NOT NULL olduğu için composite PK geçerli. DROP IF EXISTS + ADD = rerun-safe.
ALTER TABLE inflation_rates DROP CONSTRAINT IF EXISTS pk_inflation_rates;
ALTER TABLE inflation_rates ADD CONSTRAINT pk_inflation_rates PRIMARY KEY (period_date, source);

-- ─── INGR-002: ingestion_jobs inflation job desteği ────────────────────────
-- asset_id NULLABLE: EVDS bir asset değil; aylık endeks job'ı asset_id=NULL yazar.
-- FK (fk_ingestion_jobs_asset, 011) ON DELETE RESTRICT — NULL satırlar FK'yi bypass eder.
ALTER TABLE ingestion_jobs ALTER COLUMN asset_id DROP NOT NULL;

-- source: hangi dış kaynağın job'ı (provenance). asset bazlı job'larda asset.source,
-- inflation job'larında 'evds'. Nullable — geçmiş satırlarda NULL kalır.
ALTER TABLE ingestion_jobs ADD COLUMN IF NOT EXISTS source VARCHAR(30);

COMMENT ON COLUMN ingestion_jobs.asset_id IS
    'İlgili asset (price_points job''ları). Inflation job''larında NULL — aylık endeks asset değildir.';
COMMENT ON COLUMN ingestion_jobs.source IS
    'Veri kaynağı: tcmb, coingecko, openexchangerates, twelvedata, evds (provenance).';

COMMIT;
