# Price ingestion real-PostgreSQL tests

Bu proje skip kullanmaz. Repository/worker SUT yalnız explicit PostgreSQL topology,
`SAYDIN_INGESTION_DATABASE_PASSWORD_FILE` ve UUID-bound exact managed ingestion login ile çalışır.
Fixture seed/cleanup için ayrı `SAYDIN_INGESTION_TEST_DATABASE_FILE` admin secret'ı vardır; bu kimlik
SUT datasource'una aktarılmaz. Required bayrağı, run ID, exact DB adı, host allowlist'i veya rol
kontratı eksik/yanlışsa bağlantı açmadan fail-closed olur. Normal unit akışı bu projeyi çalıştırmaz.
Required CI, ayrı one-shot migrator sonrasında suite'i çalıştırır ve `ingestion-ledger.trx` için
minimum test sayısı ile `failed=skipped=0` kapısını uygular.

Suite migration 016 writer fence ile migration 017 authoritative calendar release/day,
seal/hash ve immutable window binding sözleşmelerini fail-closed doğrular. `price_points` Timescale hypertable
trigger'ı platformun desteklediği normal-enabled modda, `inflation_rates` trigger'ı
ALWAYS modunda olmalıdır. Test DB rolü fixture seed'i için ayrı ve transaction-local
bir replication-role helper kullanır; production kodunda writer-fence bypass'ı yoktur.
