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

Host SDK kullanılmaz. Pinned Docker SDK ile solution build ve yerel unit/coverage kapısı:

```bash
docker run --rm -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c \
  dotnet build Saydin.Services.sln -c Release
docker compose --env-file .env --env-file .env.database-runtime --profile test run --rm tests
infrastructure/deployment/validate-production-assets.sh
.github/scripts/validate-development-compose.sh
python3 .github/scripts/check-doc-links.py
```

Root `tests` servisi yalnız yedi unit projesini locked restore ve coverage ratchet ile çalıştırır;
solution/integration komutlarını purpose-specific DB credential taşımadığı için reddeder. Gerçek
PostgreSQL/Redis kapısının kanonik yürütmesi `.github/workflows/ci.yml` içindeki izole
`.github/compose.integration.yml` akışıdır; sıfır skip/fail şartı gevşetilmez.

Detaylı kurulum için [geliştirme rehberi](docs/development-guide.md), mimari sınırlar için
[architecture](docs/architecture.md), tamamlanmış review/remediation izi için
[analysis index](docs/analysis/README.md) kullanılır.

## Yönetişim sınırları

`CODEOWNERS` mevcut review sahibini gösterir; branch protection ve required-reviewer kuralları
GitHub repository ayarıdır ve kod deposu tek başına bunları kanıtlamaz. Proje düzeyinde bir
`LICENSE`/CLA kararı henüz seçilmemiştir. Katkı göndermek otomatik bir yeniden lisanslama veya
kullanım hakkı taahhüdü oluşturmaz; bu karar repository sahibinin dış yönetişim girdisidir.
