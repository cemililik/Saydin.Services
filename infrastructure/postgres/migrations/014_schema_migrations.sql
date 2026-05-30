-- NOT (INFR-009): docker-entrypoint init'i `psql -v ON_ERROR_STOP=1` ile çalıştırır
-- (hata → zincir durur). psql `\set` meta-komutu KULLANILMADI (SQL analyzer + psql-dışı
-- araç uyumu için).
-- ============================================================
-- Migration 014: schema_migrations izleme tablosu (F4-1 / ADR-001 — Seçenek C hybrid)
--
-- Bağlam: Şema, numaralandırılmış .sql dosyalarıyla yönetilir ve docker-entrypoint
-- yalnız BOŞ volume'da alfabetik sırayla uygular. "Hangi migration uygulandı?"
-- sorusunun denetlenebilir bir cevabı yoktu (yalnız commit hash'lerine bakılıyordu).
-- Bu tablo hafif, additive bir izleme katmanı ekler — EF Core'a tam geçişe gerek
-- kalmadan (TimescaleDB compression/hypertable EF'le modellenemez, bkz. 008b/013).
--
-- İdempotent + additive:
--   * Fresh init: tüm önceki sürümler + kendisi "uygulanmış" olarak kaydedilir.
--   * Var olan (014 öncesi) DB: bu dosya ELLE uygulanınca 001..014 geçmişini DDL
--     yeniden çalıştırmadan back-register eder (ON CONFLICT DO NOTHING). Sonrasında
--     015+ migration'lar apply-migrations.sh runner'ı ile yalnız eksik olanlar uygulanır.
--
-- ALTER COLUMN TYPE içermez → compression penceresini (008b/013) etkilemez; 013'ten
-- SONRA (alfabetik 014 > 013) güvenle çalışır. 'version' değerleri dosya adının
-- uzantısız hâlidir (apply-migrations.sh ile birebir aynı türetme).
-- ============================================================

BEGIN;

CREATE TABLE IF NOT EXISTS schema_migrations (
    version    text        PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now(),
    checksum   text        NULL
);

COMMENT ON TABLE schema_migrations IS
    'Uygulanan SQL migration sürümlerinin denetim izi (F4-1/ADR-001). version = dosya adının uzantısız hâli.';

-- ÖN-KOŞUL GUARD (F17): 001..013 back-register'ı SADECE DB 014-öncesi TAM duruma
-- ulaşmışsa geçerlidir. Ara sürümdeki (ör. 011/012) bir DB'de bu dosya ELLE çalıştırılırsa,
-- uygulanmamış 012/012b/013'ü "uygulandı" diye işaretler → apply-migrations.sh onları
-- SESSİZCE atlar → şema drift'i. Son yapısal migration 013 activity_logs compression'ını açar;
-- fresh init'te 013 alfabetik olarak 014'ten ÖNCE çalıştığından compression ZATEN açıktır →
-- guard RAISE ETMEZ. 013'e ulaşmamış DB'de kapalıdır → loud fail.
-- (timescaledb_information.hypertables.compression_enabled TS 2.16.1'de stabil public view'dır.)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM   timescaledb_information.hypertables
        WHERE  hypertable_name = 'activity_logs'
        AND    compression_enabled
    ) THEN
        RAISE EXCEPTION
            '014 on-kosulu saglanmadi: activity_logs compression KAPALI -> DB beklenen 013 durumunda degil. Once 013 dahil eksik migrationlari uygulayin, sonra 014u calistirin (fresh init bu hatayi vermez).';
    END IF;
END $$;

-- Tüm mevcut migration'ları + kendini back-register et (DDL yeniden çalıştırılmaz).
INSERT INTO schema_migrations (version) VALUES
    ('001_initial'),
    ('002_add_assets'),
    ('003_switch_precious_metals_to_oxr'),
    ('004_add_inflation_rates'),
    ('005_add_tcmb_currencies'),
    ('006_scenario_type'),
    ('007_add_dca_scenario_type'),
    ('008_add_activity_logs'),
    ('008b_disable_activity_log_compression'),
    ('009_widen_activity_log_columns'),
    ('010_add_geo_columns'),
    ('011_phase2_schema_hardening'),
    ('012_faz3_schema'),
    ('012b_create_exporter_role'),
    ('013_enable_activity_log_compression'),
    ('014_schema_migrations')
ON CONFLICT (version) DO NOTHING;

COMMIT;
