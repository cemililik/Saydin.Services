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
