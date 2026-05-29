-- INFR-009: hata durumunda zincir dursun (entrypoint -v ON_ERROR_STOP=1 verir; yeni
-- migration'larda repo-içi garanti için açıkça da set ediyoruz).
\set ON_ERROR_STOP on
-- ============================================================
-- Migration 008b: activity_logs compression'ı GEÇİCİ olarak kapat
--
-- KÖK NEDEN (review F0.1-3): 008_add_activity_logs.sql sonuna RETROAKTİF eklenen
--   ALTER TABLE activity_logs SET (timescaledb.compress, ...) + add_compression_policy
-- bloğu, hypertable'da "compression enabled" bayrağını set eder. TimescaleDB 2.16.1'de
-- bu bayrak set iken `ALTER COLUMN ... TYPE` işlemleri — chunk compress edilmiş olsun
-- olmasın — "operation not supported on hypertables that have compression enabled"
-- hatası verir (kısıt hypertable bayrağına bağlıdır, chunk durumuna değil; bkz.
-- timescaledb issue #2663). Bu nedenle FRESH init'te 009'un üç `ALTER COLUMN TYPE`'ı
-- (device_os/os_version/app_version) patlar; docker-entrypoint ON_ERROR_STOP ile zinciri
-- durdurur ve 010/011/012/012b/013 HİÇ çalışmaz. (TimescaleDB bu kısıtı yalnızca 2.24.0'da
-- "no compressed chunks" durumu için gevşetti — pinli 2.16.1 imajı etkilenir.)
--
-- ÇÖZÜM: 009'dan ÖNCE compression'ı kapat (alfabetik: 008_ < 008b < 009). 010/011/012
-- kolon değişikliklerinden SONRA migration 013 compression'ı 008'deki ayarla birebir
-- yeniden açar. Mevcut migration dosyaları DEĞİŞTİRİLMEZ (CLAUDE.md kuralı).
--
-- IDEMPOTENT / MEVCUT DB GÜVENLİĞİ: Mevcut dev/prod DB'leri compression'sız eski 008'den
-- init edildi; initdb script'leri dolu volume'da yeniden çalışmaz → bu dosya yalnız FRESH
-- init'te koşar. Yine de tüm adımlar guard'lı: if_exists / is_compressed filtresi /
-- idempotent SET. Manuel uygulansa bile güvenli no-op.
--
-- SIRA ZORUNLU (timescaledb issue #1661): (1) policy kaldır → (2) compressed chunk'ları
-- decompress et → (3) SET compress=false. Aksi halde SET'in kendisi "cannot change
-- configuration on already compressed chunks" ile fail eder. Fresh init'te chunk yok →
-- (2) no-op; ama sıra korunur ki dolu DB'de de doğru çalışsın.
-- ============================================================

BEGIN;

-- (1) 7 günlük compression policy'sini kaldır (job id'ye değil hypertable'a göre).
SELECT remove_compression_policy('activity_logs', if_exists => TRUE);

-- (2) Compress edilmiş chunk varsa decompress et. Fresh init'te 0 chunk → döngü boş.
--     Değişken adı (v_chunk) kasıtlı olarak kolon adından (chunk_name) FARKLI seçildi;
--     aksi halde PL/pgSQL kolon-değişken gölgelemesi format(...) içinde yanlış değer üretir.
DO $$
DECLARE
    v_chunk regclass;
BEGIN
    FOR v_chunk IN
        SELECT format('%I.%I', chunk_schema, chunk_name)::regclass
        FROM   timescaledb_information.chunks
        WHERE  hypertable_name = 'activity_logs'
        AND    is_compressed
    LOOP
        PERFORM decompress_chunk(v_chunk);
    END LOOP;
END $$;

-- (3) Hypertable üzerinde compression'ı kapat → 009/011 ALTER COLUMN TYPE artık serbest.
ALTER TABLE activity_logs SET (timescaledb.compress = false);

COMMIT;
