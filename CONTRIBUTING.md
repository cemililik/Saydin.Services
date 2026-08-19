# Katkı Rehberi

Katkılar `development` branch'ini hedefleyen küçük, bağımsız pull request'lerle yapılır.
Repository'nin mevcut çalışma ağacı başka geliştirmeler içerebilir; ilgisiz dosyaları
formatlamayın veya geri almayın.

## Yerel başlangıç

1. `.env.example` dosyasını `.env` olarak kopyalayın ve placeholder değerleri değiştirin.
2. `./infrastructure/secrets/bootstrap-dev-database.sh` ile purpose-specific private secret
   volume'larını ve `.env.database-runtime` metadata dosyasını üretin.
3. `docker compose --env-file .env --env-file .env.database-runtime up -d` çalıştırın.
4. Admin araçları için ayrıca `--profile devtools`; ingestion için `--profile ingestion` ve
   en az bir explicit worker seçimi kullanın.

Ham secret'ı `.env`, command argument, connection URL, container environment, log, test kanıtı
veya GitHub environment dosyasına yazmayın. Normal runtime yalnız kendi password file'ını görür.

## Değişiklik kuralları

- Migration'lar forward-only ve immutable'dır. Yeni migration; manifest, raw SHA trust-root,
  terminal count, fingerprint doğrulayıcı ve gerçek PostgreSQL negatifleriyle birlikte gelir.
- Paket sürümleri central props'ta pinlenir. Lockfile değişikliği bilinçli olarak regenerate
  edilir ve `dotnet restore --locked-mode` ile doğrulanır. High/Critical audit veya uyumsuz
  lisans kabul edilmez.
- Yeni API davranışı test, canonical architecture/ADR ve istemci örneğiyle güncellenir.
  Korumalı endpoint'ler server-issued `Authorization: Installation <token>` kullanır.
- Cache, metric ve alert label'ları bounded olmalıdır; secret, ham IP veya kullanıcı girdisi
  key/tag olamaz. Yeni alert'in gerçek bir `docs/runbooks/` bağlantısı olmalıdır.
- Birim testleri normal `dotnet test` akışında çalışır. Gereken altyapı davranışı gerçek
  PostgreSQL/Redis integration testiyle kilitlenir; sessiz skip veya fail-open fixture yoktur.

## Zorunlu kontroller

Pull request en az build, unit coverage, integration, migration/role/DQA, production-assurance,
supply-chain ve CodeQL işlerini geçmelidir. Lokal eşdeğerleri:

```bash
dotnet restore Saydin.Services.sln --locked-mode
UNIT_COVERAGE_DIR="$(mktemp -d /tmp/saydin-unit-coverage.XXXXXX)"
.github/scripts/run-unit-coverage.sh "$UNIT_COVERAGE_DIR"
infrastructure/deployment/validate-production-assets.sh
.github/scripts/validate-development-compose.sh
python3 .github/scripts/check-doc-links.py
```

Detaylı kurulum için [geliştirme rehberi](docs/development-guide.md), mimari sınırlar için
[architecture](docs/architecture.md), tamamlanmış review/remediation izi için
[analysis index](docs/analysis/README.md) kullanılır.

## Yönetişim sınırları

`CODEOWNERS` mevcut review sahibini gösterir; branch protection ve required-reviewer kuralları
GitHub repository ayarıdır ve kod deposu tek başına bunları kanıtlamaz. Proje düzeyinde bir
`LICENSE`/CLA kararı henüz seçilmemiştir. Katkı göndermek otomatik bir yeniden lisanslama veya
kullanım hakkı taahhüdü oluşturmaz; bu karar repository sahibinin dış yönetişim girdisidir.
