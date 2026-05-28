-- IP geolocation: MaxMind GeoLite2 ile çözümlenen ülke/şehir bilgisi.
-- IP maskelemeden ÖNCE çözümlenir, böylece lokasyon kaybedilmez.
--
-- NOT: Migration 008 (CHRONOLOJİK OLARAK DAHA SONRA RETROAKTIF GÜNCELLENDİ)
-- country/city kolonlarını ve `idx_activity_logs_country` indexini zaten
-- ekliyor. Bu dosya idempotent tutulmuştur (IF NOT EXISTS) — böylece:
--   - Fresh init'te 008 → 010 sıralı uygulamasında 010 no-op olur.
--   - 008'in eski (geo'suz) versiyonunu çalıştırmış mevcut DB'lerde
--     010 eksikleri tamamlar.

BEGIN;

ALTER TABLE activity_logs ADD COLUMN IF NOT EXISTS country CHAR(2);
ALTER TABLE activity_logs ADD COLUMN IF NOT EXISTS city VARCHAR(100);

-- Coğrafi dağılım raporu için index
CREATE INDEX IF NOT EXISTS idx_activity_logs_country
    ON activity_logs (country, created_at DESC);

COMMENT ON COLUMN activity_logs.country IS 'ISO 3166-1 alpha-2 ülke kodu (MaxMind GeoLite2)';
COMMENT ON COLUMN activity_logs.city IS 'Şehir adı (MaxMind GeoLite2)';

COMMIT;
