# Geliştirme Kılavuzu — Saydin.Services

## Ön Koşullar

| Araç | Versiyon | Kullanım |
|---|---|---|
| Docker Desktop | 4.x+ | Tüm altyapı servisleri için |
| .NET SDK | 10.0 | Yerel geliştirme (opsiyonel) |
| Git | 2.x | Versiyon kontrolü |

> **Not:** .NET SDK kurulu değilse tüm servisler Docker container'ı içinde çalıştırılabilir.

## 1. Altyapıyı Başlatma

```bash
# docker-compose.yml repo kökünde bulunur
cp .env.example .env
# .env dosyasını düzenle — etkinleştirdiğiniz CoinGecko, OpenExchangeRates, Twelve Data
# ve EVDS worker'larının key'lerini doldurun. Yalnız TCMB key gerektirmez.

./infrastructure/secrets/bootstrap-dev-database.sh
docker compose --env-file .env --env-file .env.database-runtime up --build -d

# Yönetim/gözlem UI'ları default stack'e dahil değildir
docker compose --env-file .env --env-file .env.database-runtime --profile devtools \
  up --build -d pgadmin redis-insight aspire-dashboard prometheus

# İsteğe bağlı: .env içinde en az bir WORKER_*_ENABLED=true seçtikten sonra
docker compose --env-file .env --env-file .env.database-runtime --profile ingestion \
  up --build -d saydin-price-ingestion
```

İlk komut root-only one-shot generator/materializer ile her DB consumer için ayrı named volume
hazırlar; source/admin secret normal runtime servislerine mount edilmez. Ardından PostgreSQL kimliği,
role prefix'i ve exact managed login adları `.env.database-runtime` içine yazılır. Bu dosya yalnız
nonsecret metadata içerir ve gitignored'dır. Doğrudan `docker compose up` metadata yoksa
`runtime-metadata-required` ile fail-closed olur. Parola `.env`, command/argv veya container
environment'a konmaz.
`saydin-price-ingestion` bilinçli olarak `ingestion` profilindedir: default stack dış provider'a
çağrı yapmaz; profil tüm worker'lar kapalıyken başlatılırsa worker güvenlik kapısı non-zero döner.

Başlatılan servisler:
- `postgres` → 127.0.0.1:5432 (TimescaleDB)
- `redis` → 127.0.0.1:6379
- `saydin-api` → http://127.0.0.1:5080
- `saydin-price-ingestion` → yalnız `ingestion` profiliyle (port expose edilmez)
- `pgadmin`, `redis-insight`, `aspire-dashboard`, `prometheus` → yalnız `devtools` profiliyle;
  sırasıyla loopback 5050, 5540, 18888 ve 9090

Sabit `container_name` kullanılmaz; bütün container/volume adları Compose proje adıyla ayrılır.
İkinci checkout için `SAYDIN_*_PORT` değişkenleriyle host portlarını farklılaştırabilirsin.

### Redis Insight Bağlantısı

1. http://localhost:5540 adresini aç
2. **"Add Redis Database"** butonuna tıkla
3. Aşağıdaki bilgileri gir:
   - **Host:** `redis` *(Docker servis adı — `localhost` değil)*
   - **Port:** `6379`
   - **Password:** `.env` içindeki `REDIS_PASSWORD`
4. **"Add Redis Database"** ile kaydet

> Redis Insight, Compose iç network'ünde çalışır. `localhost` yazılırsa kendi container'ını görür — Redis'e `redis` servis adıyla ulaşılır.

## 2. Veritabanı Migration

### İlk Kurulum (one-shot migrator — otomatik)

Güncel güvenli akış önce one-shot `Saydin.DatabaseRoleBootstrap ensure`, sonra versioned migrator
login/password-file kullanan `database-migrator`'dır. API, ingestion ve monitoring yalnız migrator
sıfır exit ile tamamlanırsa başlar. Boş DB 24 migration ile bootstrap edilir; yönetilen DB checksum,
rol grafiği, ACL ve schema fingerprint kontrolünden geçirilir. Elle `001_initial.sql` çalıştırılmaz.
Kök Compose role-bootstrap → managed migrator zincirini ve per-purpose file-secret mount'larını taşır:

```bash
docker compose --env-file .env --env-file .env.database-runtime ps --all database-migrator
docker compose --env-file .env --env-file .env.database-runtime logs database-migrator
docker compose --env-file .env --env-file .env.database-runtime run --rm database-migrator --verify-only
```

> **Readiness ayrımı:** PostgreSQL `pg_isready` healthcheck'i yalnız bağlantıyı kanıtlar.
> Şemanın kullanıma hazır olduğunu migrator'ın transaction/checksum/fingerprint doğrulaması ve
> `saydin_migration_control.state='ready'` sonucu belirler. Partial/ambiguous DB, bilinmeyen yeni
> version veya uygulanmış dosyada checksum farkı otomatik onarılmaz; migrator non-zero döner ve
> downstream kapalı kalır. Volume/drop/recreate otomatik yapılmaz.
>
> **Migration 008b/013 (Faz 3):** TimescaleDB 2.16.1'de compression **enabled** iken
> `ALTER COLUMN ... TYPE` yasaktır. `008` retroaktif compression açtığı için 009/011 ALTER'ları
> fresh init'te zinciri kırıyordu. `008b` compression'ı 009'dan önce kapatır, `013` 012'den sonra
> geri açar (mevcut migration'lar değiştirilmedi). Yeni bir `ALTER COLUMN TYPE` eklerken bu
> disable/re-enable penceresini koru. Zaten compress edilmiş **prod** tablolar için 011 üst
> yorumundaki manuel runbook geçerlidir.

### Migration Stratejisi & İzleme (ADR-001 — C-02 control-plane)

Aktif strateji **numaralandırılmış SQL** dosyalarıdır (EF Core'a tam geçiş post-MVP'ye
ertelendi — TimescaleDB compression/hypertable EF'le modellenemez; bkz.
[ADR-001](decisions/ADR-001-migration-strategy.md)). `Saydin.DatabaseRoleBootstrap` physical-target
advisory lock altında cluster-global rol grafiğini; `Saydin.DatabaseMigrator` raw-byte SHA-256,
aynı target lock'u ve runner-owned transaction'ı yönetir. `schema_migrations`
her adımın `succeeded`/`skipped_optional`/`failed` durumunu; `saydin_migration_control` tüm hedefin
`ready` durumunu taşır. Uygulanmışları görmek için:

```bash
docker compose --env-file .env --env-file .env.database-runtime exec postgres \
  psql -U saydin_admin -d saydin \
  -c "SELECT version, state, checksum, completed_at FROM schema_migrations ORDER BY version;"
```

**Yeni migration ekleme:** Sıradaki numarayla yeni `.sql` dosyası ekle (`023_*.sql`);
mevcut dosyaları **asla değiştirme**. Alfabetik sıralama 008b/013 compression penceresini
bozmamalı (`014`+ güvenle 013 sonrası sıralanır).

**Var olan (boş olmayan / prod) DB'ye deploy:**

```bash
docker compose --env-file .env --env-file .env.database-runtime up --build -d
docker compose --env-file .env --env-file .env.database-runtime run --rm database-migrator --verify-only
```

Complete-014 veya managed-through-018 legacy DB yalnız explicit
`--legacy-privilege-cutover --admin-connection-file` yolunda kabul edilir; business data yeniden
yazılmaz. Partial/ambiguous veya 014 öncesi DB otomatik baseline edilmez. Böyle bir
red durumunda servisleri zorla başlatma, geçmiş migration'ları değiştirme veya DB'yi drop/recreate
etme; staging clone üzerinde audit/backfill planını tamamlayıp yeni additive migration üret.
`infrastructure/postgres/apply-migrations.sh` yalnız retired compatibility entrypoint'tir;
fail-closed biçimde exit 64 döner ve hiçbir migration uygulamaz. Normal Compose/CI deploy yolunda
çağrılmaz, recovery alternatifi olarak kullanılmaz.

### EF Core ile Yeni Migration Ekleme (post-MVP — şu an KULLANILMIYOR)

> **Durum (ADR-001 revizyonu):** EF Core Migrations'a tam geçiş **ertelenmiş gelecek
> yoludur**; şu an aktif değildir (aktif strateji yukarıdaki numaralı SQL'dir). Aşağıdaki
> komutlar geçiş yapıldığında geçerli olacaktır. `Microsoft.EntityFrameworkCore.Design`
> paketi `Saydin.Api.csproj`'da geçişe hazır olarak mevcuttur.
>
> **Çalıştırma (Docker-Compose-only):** Lokal makinede .NET 10 SDK yoktur (CLAUDE.md) →
> bu komutlar da diğer `dotnet` işlemleri gibi **SDK imajı + repo mount** içinde çalışır
> (build komutuyla aynı desen). `dotnet-ef` global tool imajda yoktur, önce kurulur;
> `database update` ayrıca DB için compose ağına (`saydin-services_default`) bağlanır.

```bash
# (Post-MVP) Yeni migration oluştur — SDK imajında (lokal `dotnet ef` YOK):
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c \
  'dotnet tool install -g dotnet-ef >/dev/null 2>&1; export PATH="$PATH:/root/.dotnet/tools"; \
   dotnet ef migrations add <MigrationAdı> --project src/Saydin.Shared --startup-project src/Saydin.Api'

# DB güncellemesi `dotnet ef database update` ile yapılmaz. Additive SQL migration eklenir ve
# managed one-shot migrator çalıştırılır:
docker compose --env-file .env --env-file .env.database-runtime run --rm database-migrator
```

## 3. Saydin.Api Çalıştırma

### Docker ile

```bash
# Standalone `docker run` yerine aynı datasource/secret/identity sınırını taşıyan Compose servisini kullan:
docker compose --env-file .env --env-file .env.database-runtime up --build -d saydin-api
```

### .NET SDK ile (Yerel)

```bash
# Raw DB connection string/user-secret kabul edilmez. Explicit PGHOST/PGPORT/PGDATABASE/PGUSER/
# PGSSLMODE, SAYDIN_DATABASE_* kimlik metadata'sı ve absolute
# SAYDIN_API_DATABASE_PASSWORD_FILE ile çalıştırılmalıdır. Owner-only file'ın yerel UID'ye ait
# olması zorunludur. Taşınabilir varsayılan yukarıdaki Compose akışıdır.
```

## 4. Saydin.PriceIngestion Çalıştırma

```bash
# DB için API ile aynı explicit topology/identity sınırı ve
# SAYDIN_INGESTION_DATABASE_PASSWORD_FILE kullanılır. API anahtarları için user-secrets kullanılabilir:
dotnet user-secrets set "ExternalApis:CoinGecko:ApiKey" "<your-key>" \
  --project src/Saydin.PriceIngestion
dotnet user-secrets set "ExternalApis:OpenExchangeRates:AppId" "<your-app-id>" \
  --project src/Saydin.PriceIngestion
dotnet user-secrets set "ExternalApis:TwelveData:ApiKey" "<your-key>" \
  --project src/Saydin.PriceIngestion
dotnet user-secrets set "ExternalApis:Evds:ApiKey" "<your-key>" \
  --project src/Saydin.PriceIngestion
# TCMB için API key gerekmez

dotnet run --project src/Saydin.PriceIngestion
```

> **Not:** Key gerektiren bir worker etkinse eksik/boş key host başlamadan reddedilir;
> pencere açılmaz ve HTTP çağrısı yapılmaz. Disabled worker için key boş kalabilir.
> OpenExchangeRates ücretsiz planda aylık 1000 istek sınırı vardır.

### Worker Seçici Aktivasyonu (F4-11)

İngestion worker'ları **her katmanda varsayılan KAPALI** (disabled-by-default):
`appsettings.json` baseline `Enabled=false`, `IngestionOrchestrator` fallback `?? false`
(eksik/typo'lu config → fail-closed). Aktivasyon **env tek opt-in kaynağıdır** — `.env`:

```bash
WORKER_TCMB_ENABLED=true        # TCMB (key gerektirmez)
WORKER_EVDS_ENABLED=true        # EVDS enflasyon (EVDS_API_KEY zorunlu)
WORKER_COINGECKO_ENABLED=false  # key gerektirir
WORKER_OXR_ENABLED=false        # key gerektirir
WORKER_TWELVEDATA_ENABLED=false # key gerektirir
```

Fresh-checkout `.env.example` TCMB + EVDS'i açık gönderir; bu nedenle ingestion profili
başlatılmadan önce `EVDS_API_KEY` doldurulmalıdır. Diğer key gerektiren kaynaklar kapalıdır.
Hiçbir
worker etkin değilse `IngestionOrchestrator` **fail-fast** yapar (`LogCritical` +
`InvalidOperationException` → host başlatılmaz); en az bir worker `IngestionWorkers:*:Enabled`
(örn. `WORKER_TCMB_ENABLED`) ile açılmalıdır — böylece "boş" bir ingestion servisi sessizce
çalışıyormuş gibi görünmez.

### GeoIP (opsiyonel, F4-7)

`activity_logs` coğrafi zenginleştirmesi MaxMind **GeoLite2-City** ile yapılır. `.mmdb`
**repoya commit edilmez** (lisans); `infrastructure/geoip/README.md`'deki komutla
`GEOIP_ACCOUNT_ID`/`GEOIP_LICENSE_KEY` kullanılarak indirilir. Dosya yoksa GeoIP devre dışı
kalır (`LogWarning` + `country`/`city` null) — **istek başarısız olmaz**. Detay:
[ADR-004](decisions/ADR-004-geoip-distribution.md).

## 5. Testleri Çalıştırma

Tekrarlanabilir Docker-first yol **`tests` compose profili**dir (pinned SDK imajı + repo mount +
compose ağı). Host SDK kullanılsa bile `global.json`, locked restore ve aynı test projeleri
korunmalıdır; `saydin-api` runtime imajı SDK/test projeleri içermez.

```bash
# Lokal optional mod: tüm solution. Integration için postgres/redis up olmalı;
# infra yoksa yalnız integration testleri Skipped olabilir.
docker compose --env-file .env --env-file .env.database-runtime up -d postgres database-migrator redis
docker compose --env-file .env --env-file .env.database-runtime --profile test run --rm tests

# Yalnız unit testler (DB gerekmez)
docker compose --env-file .env --env-file .env.database-runtime --profile test run --rm tests test tests/Saydin.Api.Tests
docker compose --env-file .env --env-file .env.database-runtime --profile test run --rm tests test tests/Saydin.PriceIngestion.Tests

# Gerçek PostgreSQL/Redis entegrasyon testleri (F2.6-21)
docker compose --env-file .env --env-file .env.database-runtime --profile test run --rm tests test tests/Saydin.Api.IntegrationTests

# Sadece build doğrulaması (compose'suz, SDK imajı + mount)
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Saydin.Services.sln -c Debug
```

> **Integration testleri (F2.6-21 / RP-02):** Yukarıdaki geliştirici akışı **optional**
> moddur; DB/Redis erişilemezse `SkippableFact` ile atlama kolaylığı korunur. Required CI job'ı
> ise `.github/compose.integration.yml` ile host portu açmadan UUID-bazlı disposable
> iki ayrı TimescaleDB + Redis kurar, `SAYDIN_INTEGRATION_REQUIRED=true` kullanır ve
> infra/guard hatasında fail-fast olur. CI ayrıca role-bootstrap + migrator `--verify-only`, fresh
> `24 migration + 2 hypertable + 24 checksum + 24 terminal + ready` kapısını; API integration
> TRX'inde en az 57, ingestion ledger TRX'inde 39, role-bootstrap TRX'lerinde 76+7, migrator
> TRX'inde 124 ve DQA TRX'lerinde 82+72 executed test ile sıfır failed/skipped/notExecuted şartını
> doğrular. Kanonik executable adımlar
> `.github/workflows/ci.yml` içindeki `integration-test` job'ındadır.

## 6. Sık Kullanılan Komutlar

```bash
# Aşağıdaki dotnet komutları yalnız lokalde .NET 10 SDK kuruluysa çalışır; kurulu
# değilse SDK imajı + repo mount kullan (bkz. Adım 5 build doğrulaması).

# Bağımlılıkları yükle
dotnet restore --locked-mode

# Build
dotnet build

# Tüm container'ları durdur (repo kökünden)
docker compose --env-file .env --env-file .env.database-runtime down

# PostgreSQL'e bağlan
docker compose --env-file .env --env-file .env.database-runtime exec postgres psql -U saydin_admin -d saydin

# Redis CLI
docker compose --env-file .env --env-file .env.database-runtime exec redis redis-cli

# Log izle (Aspire Dashboard yerine terminal)
docker compose --env-file .env --env-file .env.database-runtime logs --follow saydin-api | jq .
```

## 7. API Test Örnekleri

```bash
# Process liveness (public listener)
curl -fsS http://localhost:5080/health/live

# Server-issued bearer credential üret; token'ı loglama veya repoya yazma.
INSTALLATION_TOKEN="$(curl -fsS -X POST http://localhost:5080/v1/installations | jq -er .credential)"
AUTHORIZATION="Authorization: Installation $INSTALLATION_TOKEN"

# Asset listesi
curl -H "$AUTHORIZATION" http://localhost:5080/v1/assets | jq

# Tek gün fiyat
curl -H "$AUTHORIZATION" "http://localhost:5080/v1/assets/USDTRY/price/2020-01-01" | jq

# Fiyat aralığı
curl -H "$AUTHORIZATION" "http://localhost:5080/v1/assets/USDTRY/price-range?from=2020-01-01&to=2020-12-31" | jq

# "Ya alsaydım" hesaplama
curl -X POST http://localhost:5080/v1/what-if/calculate \
  -H "Content-Type: application/json" \
  -H "$AUTHORIZATION" \
  -d '{
    "assetSymbol": "USDTRY",
    "buyDate": "2020-01-01",
    "sellDate": "2024-01-01",
    "amount": 10000,
    "amountType": "TRY"
  }' | jq

# Karşılaştırma (Compare)
curl -X POST http://localhost:5080/v1/what-if/compare \
  -H "Content-Type: application/json" \
  -H "$AUTHORIZATION" \
  -d '{
    "assetSymbols": ["USDTRY", "BTCTRY"],
    "buyDate": "2020-01-01",
    "sellDate": "2024-01-01",
    "amount": 10000,
    "amountType": "TRY"
  }' | jq

# Ters senaryo (Reverse What-If)
curl -X POST http://localhost:5080/v1/what-if/reverse \
  -H "Content-Type: application/json" \
  -H "$AUTHORIZATION" \
  -d '{
    "assetSymbol": "USDTRY",
    "buyDate": "2020-01-01",
    "sellDate": "2024-01-01",
    "targetAmount": 50000,
    "targetAmountType": "TRY"
  }' | jq

# DCA hesaplama
curl -X POST http://localhost:5080/v1/what-if/dca \
  -H "Content-Type: application/json" \
  -H "$AUTHORIZATION" \
  -d '{
    "assetSymbol": "USDTRY",
    "startDate": "2020-01-01",
    "endDate": "2024-01-01",
    "periodicAmount": 1000,
    "amountType": "TRY",
    "period": "monthly",
    "includeInflation": false
  }' | jq

# Senaryo kaydet
curl -X POST http://localhost:5080/v1/scenarios \
  -H "Content-Type: application/json" \
  -H "$AUTHORIZATION" \
  -d '{
    "type": "what_if",
    "assetSymbol": "USDTRY",
    "title": "USD Test",
    "parameters": {"buyDate": "2020-01-01", "amount": 10000}
  }' | jq

# Senaryoları listele
curl http://localhost:5080/v1/scenarios \
  -H "$AUTHORIZATION" | jq

# Uygulama konfigürasyonu
curl http://localhost:5080/v1/config \
  -H "$AUTHORIZATION" | jq

# Prometheus metrikleri
docker compose --env-file .env --env-file .env.database-runtime \
  exec saydin-api curl -fsS http://127.0.0.1:9090/metrics
```

## 8. Ortam Değişkenleri

| Değişken | Açıklama | Örnek |
|---|---|---|
| `PGHOST` / `PGPORT` / `PGDATABASE` / `PGUSER` / `PGSSLMODE` | Nonsecret PostgreSQL topology ve exact managed login | `postgres` / `5432` / `saydin` / `<prefix>_api_login_v1` / `Disable` |
| `SAYDIN_<PURPOSE>_DATABASE_PASSWORD_FILE` | Consumer'a özel absolute owner-only password file | `/run/saydin-secrets/api-v1` |
| `SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256` / `SAYDIN_DATABASE_ROLE_PREFIX` | DB identity ve rol kontratı | 64-hex / bounded prefix |
| `ConnectionStrings__Redis` | Redis bağlantı dizesi | `localhost:6379` |
| `Otlp__Endpoint` | OTLP collector endpoint | `http://localhost:4317` |
| `ExternalApis__CoinGecko__ApiKey` | CoinGecko API anahtarı | Worker enabled ise zorunlu |
| `ExternalApis__OpenExchangeRates__AppId` | Open Exchange Rates App ID | Worker enabled ise zorunlu |
| `ExternalApis__TwelveData__ApiKey` | Twelve Data API anahtarı | Worker enabled ise zorunlu |
| `ExternalApis__Evds__ApiKey` | TCMB EVDS API anahtarı | Worker enabled ise zorunlu |
| `DistributedSecurityLimiter__Enabled` | Dağıtık IP/network/principal limiter; production'da zorunlu | `true` |
| `DistributedSecurityLimiter__HmacKeyFile` | Pseudonym key'i taşıyan absolute private file | `/run/saydin-secrets/private/security-limiter-hmac` |
| `DistributedSecurityLimiter__ExactIpLimit` / `NetworkLimit` / `PrincipalLimit` | Redis TIME penceresindeki üç ayrı üst sınır | `60` / `300` / `120` |
| `DistributedSecurityLimiter__WindowSeconds` | Atomik Redis pencere uzunluğu | `60` |
| `InstallationCredentials__SecretFile` | Installation credential HMAC keyring private file | `/run/saydin-secrets/private/installation-keyring.json` |
| `ForwardedHeaders__KnownProxies` | Güvenilen reverse-proxy IP'leri (CSV) | `10.0.0.5` |
| `ForwardedHeaders__KnownNetworks` | Güvenilen subnet'ler (CIDR, CSV) | `10.0.0.0/8` |
| `GeoIp__DatabasePath` | MaxMind GeoLite2 `.mmdb` yolu (opsiyonel) | `/app/geoip/GeoLite2-City.mmdb` |

> **Rate limiting ([ADR-003](decisions/ADR-003-rate-limiting.md)):** Production validator limiter'ın
> açık, HMAC file'ın private ve trusted proxy CIDR'nin bounded olmasını zorunlu kılar. Exact IP,
> IPv4 /24 veya IPv6 /64 network ve authenticated installation principal ayrı Redis TIME
> bucket'larıdır. Client IP güvenilir biçimde çözülemezse veya Redis admission sırasında
> kullanılamazsa 503; limit aşılırsa 429 döner. Kota ve security limiter fail-open değildir.

> **Güvenlik:** API key'leri asla `appsettings.json`'a yazmayın. DB parolaları environment,
> connection URL, argv veya log'a konmaz; yalnız strict file-secret kullanılır. Dış provider
> secret'ları için geliştirmede `dotnet user-secrets`, production'da secret backend kullanın. Tam strateji (dev/CI/prod katmanları + rotation
> runbook): [ADR-005](decisions/ADR-005-secrets-management.md).

## 9. CI/CD — GitHub Actions

Her `push` ve `pull_request`'te otomatik çalışır:

1. **Build** — `dotnet build`
2. **Unit/coverage** — altı gerçek unit csproj; Cobertura weighted line ≥ %75, branch ≥ %60 ve
   kritik namespace ratchet'ları. Changed executable-line ≥ %80 kapısı, aynı kaynak satırlarını
   tekilleştiren altı unit ve dört required gerçek-infrastructure Cobertura raporu üzerinde uygulanır.
   Eksik/bozuk rapor veya rapor sayısı uyuşmazlığı kapıyı kapatır. RoleBootstrap'ın SQL/ACL ağırlıklı
   yüzeyi için düşük unit tabanı tek başına kabul sayılmaz; ayrı 7/7 gerçek-PostgreSQL kapısı zorunludur.
3. **Required integration** — disposable TimescaleDB/Redis, one-shot migrator verify ve fail-closed TRX gate'leri
4. **Platform/supply chain** — production render/mutation, Prometheus/Alertmanager/OTel/Caddy,
   NuGet audit+locked graph, dependency/license/vulnerability/secret/IaC ve CodeQL
5. **Docker Build** — API, ingestion ve database-control image'larını oluşturur

CI pipeline `.github/workflows/` dizininde tanımlıdır.

**Otomatik PR incelemesi (F4-12):** Pull request'ler **CodeRabbit** ile incelenir
(yapılandırma: `.coderabbit.yaml`; `base_branches: main + development`; SQL migration'lar ve
`docs/**` review kapsamındadır). Sourcery kullanılmaz (.NET için anlamlı değil — F1.8-4 ile
kaldırıldı). Ek statik analiz: Codacy (`.codacy.yaml`).

---

## 10. Yaygın Sorunlar

### "Connection refused" — PostgreSQL

```bash
# Compose servisi çalışıyor mu?
docker compose --env-file .env --env-file .env.database-runtime ps postgres

# Bağlantı testi
docker compose --env-file .env --env-file .env.database-runtime exec postgres \
  pg_isready -U saydin
```

### Migration uygulanmamış

```bash
# Tablo var mı?
docker compose --env-file .env --env-file .env.database-runtime exec postgres \
  psql -U saydin -d saydin -c "\dt"
# Yoksa migration adımını tekrarla (bkz. Adım 2)
```

### Aspire Dashboard'da trace yok

OTLP bağlantısını doğrula:
```bash
# `devtools` profili etkin mi?
docker compose --env-file .env --env-file .env.database-runtime \
  --profile devtools ps aspire-dashboard

# App'in OTLP'ye bağlandığını log'dan kontrol et
docker compose --env-file .env --env-file .env.database-runtime logs saydin-api 2>&1 | grep -i otlp
```
