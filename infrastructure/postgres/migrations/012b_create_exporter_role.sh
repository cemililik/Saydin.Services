#!/bin/bash
# ============================================================
# Migration 012b: INFR-005 — postgres-exporter least-privilege rolü
#
# `saydin_exporter`: yalnız metrik okuyabilen (pg_monitor) login rolü. Böylece
# postgres-exporter CRUD-yetkili `saydin` user'ı yerine least-privilege bir rol
# kullanır (production güvenlik gereği).
#
# Parola SQL dosyasına YAZILMAZ (CLAUDE.md: sır dosyaya yazılmaz). Bunun yerine
# bu script POSTGRES_EXPORTER_PASSWORD env'inden okur ve psql değişkeni (%L ile
# güvenli alıntılanmış) olarak geçer. Env set DEĞİLSE rol yaratılmaz ve exporter
# compose default'u ile `saydin` user'ına geri düşer (geriye dönük uyumluluk;
# fresh dev kurulumu kırılmaz).
#
# Bu script docker-entrypoint-initdb.d içinde YALNIZCA ilk init'te (boş volume)
# çalışır. Mevcut DB'de rol manuel yaratılır (bkz. 012_faz3_schema.sql runbook).
# Alfabetik sıra: "012_faz3_schema.sql" < "012b_create_exporter_role.sh" → şema önce.
# ============================================================
set -euo pipefail

if [ -z "${POSTGRES_EXPORTER_PASSWORD:-}" ]; then
    echo "[012b] POSTGRES_EXPORTER_PASSWORD set değil — saydin_exporter rolü atlandı (exporter POSTGRES_USER'a düşer)."
    exit 0
fi

echo "[012b] saydin_exporter rolü oluşturuluyor/güncelleniyor (pg_monitor)..."

# Quoted heredoc ('EOSQL') → bash interpolasyonu yok; :'exporter_pw' psql tarafından
# güvenli string literal'e açılır. \if/\gset dollar-quote DIŞINDA çalıştığı için
# parola interpolasyonu sorunsuzdur (DO bloğu içinde :var açılmaz).
psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" \
     --dbname "$POSTGRES_DB" \
     --set=exporter_pw="$POSTGRES_EXPORTER_PASSWORD" <<-'EOSQL'
    SELECT CASE WHEN EXISTS (SELECT FROM pg_roles WHERE rolname = 'saydin_exporter')
                THEN 'false' ELSE 'true' END AS need_create \gset
    \if :need_create
        CREATE ROLE saydin_exporter LOGIN PASSWORD :'exporter_pw';
    \else
        ALTER ROLE saydin_exporter WITH LOGIN PASSWORD :'exporter_pw';
    \endif
    GRANT pg_monitor TO saydin_exporter;
EOSQL

echo "[012b] saydin_exporter rolü hazır."
