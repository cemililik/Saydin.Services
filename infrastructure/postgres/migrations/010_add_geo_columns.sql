-- IP geolocation: MaxMind GeoLite2 ile çözümlenen ülke/şehir bilgisi.
-- IP maskelemeden ÖNCE çözümlenir, böylece lokasyon kaybedilmez.

BEGIN;

ALTER TABLE activity_logs ADD COLUMN country CHAR(2);
ALTER TABLE activity_logs ADD COLUMN city VARCHAR(100);

-- Coğrafi dağılım raporu için index
CREATE INDEX idx_activity_logs_country
    ON activity_logs (country, created_at DESC);

COMMENT ON COLUMN activity_logs.country IS 'ISO 3166-1 alpha-2 ülke kodu (MaxMind GeoLite2)';
COMMENT ON COLUMN activity_logs.city IS 'Şehir adı (MaxMind GeoLite2)';

COMMIT;
