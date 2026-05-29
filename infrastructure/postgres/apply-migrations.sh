#!/usr/bin/env bash
# ============================================================
# F4-1 / F4-8 (ADR-001 — Seçenek C hybrid): VAR OLAN (boş olmayan) bir PostgreSQL
# veritabanına YENİ migration'ları uygulayan idempotent runner.
#
# Fresh/boş DB'ler docker-entrypoint ile `/docker-entrypoint-initdb.d` üzerinden kurulur
# (compose `./infrastructure/postgres/migrations` klasörünü oraya mount eder). Bu script
# o klasörün DIŞINDADIR ve initdb.d'ye MOUNT EDİLMEZ → init sırasında ASLA otomatik çalışmaz.
# Production/staging gibi var olan DB'lerde DEPLOY adımı olarak elle (ya da CI/Job ile) çağrılır.
#
# Mantık: schema_migrations tablosuna bakar; KAYITLI OLMAYAN .sql/.sh dosyalarını alfabetik
# sırada `psql -v ON_ERROR_STOP=1` ile uygular ve başarı sonrası version'ı kaydeder.
# version = dosya adının uzantısız hâli (014_schema_migrations.sql back-register'ı ile aynı türetme).
#
# ÖNEMLİ: 014 ÖNCESİ var olan DB'lerde ÖNCE `014_schema_migrations.sql` elle uygulanmalıdır
# (001..014 geçmişini DDL yeniden çalıştırmadan back-register eder). Sonrasında bu runner
# yalnız 015+ migration'ları uygular — eski migration'ları RE-RUN ETMEZ (idempotency garantisi).
#
# Kullanım:
#   DATABASE_URL='postgres://user:pass@host:5432/db' ./apply-migrations.sh
#   # veya psql ortam değişkenleri (PGHOST/PGUSER/PGPASSWORD/PGDATABASE) ile DATABASE_URL boş bırakılabilir.
# ============================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MIGRATIONS_DIR="${SCRIPT_DIR}/migrations"

# DATABASE_URL verilmemişse psql kendi PG* env değişkenlerini kullanır (boş arg geçeriz).
PSQL_TARGET="${DATABASE_URL:-}"

run_psql() {
    if [[ -n "${PSQL_TARGET}" ]]; then
        psql "${PSQL_TARGET}" -v ON_ERROR_STOP=1 "$@"
    else
        psql -v ON_ERROR_STOP=1 "$@"
    fi
}

# schema_migrations yoksa oluştur (014 öncesi DB'ler için güvenli; 014 zaten IF NOT EXISTS).
run_psql -q -c \
    "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now(), checksum text NULL);"

applied_count=0
for path in "${MIGRATIONS_DIR}"/*; do
    file="$(basename "${path}")"
    case "${file}" in
        *.sql|*.sh) ;;            # yalnız .sql / .sh
        *) continue ;;
    esac
    version="${file%.*}"          # uzantısız ad = version

    already="$(run_psql -tA -c "SELECT 1 FROM schema_migrations WHERE version = '${version}';")"
    if [[ "${already}" == "1" ]]; then
        echo "↷ atlanıyor (uygulanmış): ${file}"
        continue
    fi

    echo "→ uygulanıyor: ${file}"
    if [[ "${file}" == *.sh ]]; then
        bash "${path}"            # .sh migration kendi psql çağrısını/ortamını yönetir (örn. 012b)
    else
        run_psql -f "${path}"
    fi
    run_psql -q -c \
        "INSERT INTO schema_migrations(version) VALUES ('${version}') ON CONFLICT (version) DO NOTHING;"
    applied_count=$((applied_count + 1))
done

echo "✓ Tamamlandı — ${applied_count} yeni migration uygulandı."
