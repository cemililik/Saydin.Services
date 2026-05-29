-- ============================================================
-- Migration 013: activity_logs compression'ını GERİ AÇ (008b'nin kapattığını restore et)
--
-- 008b ile fresh init'te compression geçici olarak kapatıldı; böylece 009 (kolon
-- genişletme) ve 011 (duration_ms → BIGINT) `ALTER COLUMN TYPE` ifadeleri TimescaleDB
-- 2.16.1'de çalışabildi. Tüm kolon-tip değişiklikleri bittikten SONRA (alfabetik:
-- 013 > 012b > 012 > 011) 008'in hedeflediği depolama davranışını yeniden kurar.
--
-- segmentby / orderby değerleri 008'deki orijinal ayarla BİREBİR aynı tutulur — aksi
-- halde fresh ve mevcut DB'ler farklı compression layout'una düşer (storage drift).
--
-- IDEMPOTENT: SET (compress, ...) tekrar çağrılabilir; add_compression_policy
-- if_not_exists => TRUE ile rerun-safe (mevcut policy varsa duplicate hatası vermez).
-- Mevcut dev/prod DB'leri (compression'sız, kolonları zaten geniş) init'te bu dosyayı
-- çalıştırmaz; manuel uygulanırsa guard'lar sayesinde güvenlidir (ve 011 prod runbook'unun
-- compression yeniden-açma adımının yerini alır — çift uygulamayı önlemek için bkz.
-- PHASE-3-DOC-UPDATE-NOTES).
--
-- NOT (compression is physical): EF Core modeli (ActivityLogConfiguration) compression'ı
-- modellemez; bu migration EF tarafında karşılık gerektirmez.
-- ============================================================

BEGIN;

-- 7 günden eski chunk'lar otomatik sıkıştırılsın (008 ile birebir aynı ayar).
ALTER TABLE activity_logs SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'action',
    timescaledb.compress_orderby   = 'created_at DESC'
);

SELECT add_compression_policy('activity_logs', INTERVAL '7 days', if_not_exists => TRUE);

COMMIT;
