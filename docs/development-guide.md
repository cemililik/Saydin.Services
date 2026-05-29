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
# docker-compose.yml bu dizinde (src/Saydin.Services/) bulunur
cp .env.example .env
# .env dosyasını düzenle — API key'leri doldur (CoinGecko, GoldAPI, Twelve Data)

docker-compose up -d
```

Başlatılan servisler:
- `saydin-postgres` → localhost:5432 (TimescaleDB)
- `saydin-redis` → localhost:6379
- `saydin-pgadmin` → http://localhost:5050 (kullanıcı: admin@saydin.dev / admin)
- `saydin-redis-insight` → http://localhost:5540 *(Redis Insight — bkz. bağlantı adımları aşağıda)*
- `aspire-dashboard` → http://localhost:18888 (traces, logs, metrics)
- `prometheus` → http://localhost:9090
- `saydin-api` → http://localhost:5080
- `saydin-price-ingestion` → (port expose edilmez, arka planda çalışır)

### Redis Insight Bağlantısı

1. http://localhost:5540 adresini aç
2. **"Add Redis Database"** butonuna tıkla
3. Aşağıdaki bilgileri gir:
   - **Host:** `redis` *(Docker servis adı — `localhost` değil)*
   - **Port:** `6379`
   - Şifre yok, boş bırak
4. **"Add Redis Database"** ile kaydet

> Redis Insight, Compose iç network'ünde çalışır. `localhost` yazılırsa kendi container'ını görür — Redis'e `redis` servis adıyla ulaşılır.

## 2. Veritabanı Migration

### İlk Kurulum (SQL dosyası ile)

```bash
# Temel şemayı (TimescaleDB extension, enum, tablolar) uygula
docker exec -i saydin-postgres psql -U saydin -d saydin \
  < src/Saydin.Services/infrastructure/postgres/migrations/001_initial.sql

# Başarı doğrulama
docker exec saydin-postgres psql -U saydin -d saydin \
  -c "\dt" | grep -E "assets|price_points|users"
```

> **Fresh init (tüm migration'lar):** `infrastructure/postgres/migrations/*.sql` dosyaları
> docker-entrypoint tarafından **yalnız boş volume'da** alfabetik + `ON_ERROR_STOP` ile
> çalışır. Tüm zinciri (001→014) temiz uygulamak için:
> ```bash
> docker compose down -v && docker compose up -d   # DİKKAT: dev volume'larını sıfırlar
> docker compose logs postgres | grep -E "running /docker-entrypoint|ERROR"  # abort yok mu?
> ```
> **Migration 008b/013 (Faz 3):** TimescaleDB 2.16.1'de compression **enabled** iken
> `ALTER COLUMN ... TYPE` yasaktır. `008` retroaktif compression açtığı için 009/011 ALTER'ları
> fresh init'te zinciri kırıyordu. `008b` compression'ı 009'dan önce kapatır, `013` 012'den sonra
> geri açar (mevcut migration'lar değiştirilmedi). Yeni bir `ALTER COLUMN TYPE` eklerken bu
> disable/re-enable penceresini koru. Zaten compress edilmiş **prod** tablolar için 011 üst
> yorumundaki manuel runbook geçerlidir.

### Migration Stratejisi & İzleme (F4-1 / ADR-001 — Seçenek C hybrid)

Aktif strateji **numaralandırılmış SQL** dosyalarıdır (EF Core'a tam geçiş post-MVP'ye
ertelendi — TimescaleDB compression/hypertable EF'le modellenemez; bkz.
[ADR-001](decisions/ADR-001-migration-strategy.md)). `014_schema_migrations.sql` hafif bir
**izleme tablosu** ekler (`schema_migrations(version, applied_at, checksum)`); fresh init
tüm sürümleri back-register eder. Uygulanmışları görmek için:

```bash
docker compose exec postgres psql -U saydin -d saydin \
  -c "SELECT version, applied_at FROM schema_migrations ORDER BY version;"
```

**Yeni migration ekleme:** Sıradaki numarayla yeni `.sql` dosyası ekle (`015_*.sql`);
mevcut dosyaları **asla değiştirme**. Alfabetik sıralama 008b/013 compression penceresini
bozmamalı (`014`+ güvenle 013 sonrası sıralanır).

**Var olan (boş olmayan / prod) DB'ye deploy (F4-8):**

```bash
DATABASE_URL='postgres://user:pass@host:5432/db' infrastructure/postgres/apply-migrations.sh
```

Runner `schema_migrations`'a bakıp yalnız **kayıtlı olmayan** migration'ları uygular
(initdb.d dışındadır → fresh init'te otomatik çalışmaz). 014-öncesi DB'lerde önce `014`
elle uygulanmalı (geçmişi back-register eder).

### EF Core ile Yeni Migration Ekleme (post-MVP — şu an KULLANILMIYOR)

> **Durum (ADR-001 revizyonu):** EF Core Migrations'a tam geçiş **ertelenmiş gelecek
> yoludur**; şu an aktif değildir (aktif strateji yukarıdaki numaralı SQL'dir). Aşağıdaki
> komutlar geçiş yapıldığında geçerli olacaktır. `Microsoft.EntityFrameworkCore.Design`
> paketi `Saydin.Api.csproj`'da geçişe hazır olarak mevcuttur.

```bash
# (Post-MVP) Yeni migration oluştur (Saydin.Shared projesine, Saydin.Api startup projesi olarak)
dotnet ef migrations add <MigrationAdı> \
  --project src/Saydin.Shared \
  --startup-project src/Saydin.Api

# (Post-MVP) Veritabanını güncelle
dotnet ef database update \
  --project src/Saydin.Shared \
  --startup-project src/Saydin.Api
```

## 3. Saydin.Api Çalıştırma

### Docker ile

```bash
cd src/Saydin.Services
docker build -f src/Saydin.Api/Dockerfile -t saydin-api .
docker run -p 5080:8080 \
  -e ConnectionStrings__Postgres="Host=host.docker.internal;Database=saydin;Username=saydin;Password=<YOUR_PASSWORD>" \
  -e ConnectionStrings__Redis="host.docker.internal:6379" \
  -e Otlp__Endpoint="http://host.docker.internal:4317" \
  saydin-api
```

### .NET SDK ile (Yerel)

```bash
cd src/Saydin.Services

# User secrets kurulumu (ilk seferinde)
dotnet user-secrets init --project src/Saydin.Api
dotnet user-secrets set "ConnectionStrings:Postgres" \
  "Host=localhost;Database=saydin;Username=saydin;Password=<YOUR_PASSWORD>" \
  --project src/Saydin.Api
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379" \
  --project src/Saydin.Api

dotnet run --project src/Saydin.Api
# → http://localhost:5080
# → Scalar API dokümantasyonu: http://localhost:5080/scalar/v1 (Development modunda)
```

## 4. Saydin.PriceIngestion Çalıştırma

```bash
# .NET ile
dotnet user-secrets init --project src/Saydin.PriceIngestion
dotnet user-secrets set "ConnectionStrings:Postgres" \
  "Host=localhost;Database=saydin;Username=saydin;Password=<YOUR_PASSWORD>" \
  --project src/Saydin.PriceIngestion
dotnet user-secrets set "ExternalApis:CoinGecko:ApiKey" "<your-key>" \
  --project src/Saydin.PriceIngestion
dotnet user-secrets set "ExternalApis:OpenExchangeRates:AppId" "<your-app-id>" \
  --project src/Saydin.PriceIngestion
dotnet user-secrets set "ExternalApis:TwelveData:ApiKey" "<your-key>" \
  --project src/Saydin.PriceIngestion
# TCMB için API key gerekmez

dotnet run --project src/Saydin.PriceIngestion
```

> **Not:** API key eksikse ilgili adapter graceful skip yapar (servisi durdurmaz). CoinGecko key olmadan 403 alınır ve adapter atlanır. OpenExchangeRates ücretsiz planda aylık 1000 istek sınırı vardır.

### Worker Seçici Aktivasyonu (F4-11)

İngestion worker'ları **her katmanda varsayılan KAPALI** (disabled-by-default):
`appsettings.json` baseline `Enabled=false`, `IngestionOrchestrator` fallback `?? false`
(eksik/typo'lu config → fail-closed). Aktivasyon **env tek opt-in kaynağıdır** — `.env`:

```bash
WORKER_TCMB_ENABLED=true        # TCMB (key gerektirmez)
WORKER_EVDS_ENABLED=true        # EVDS enflasyon (key gerektirmez)
WORKER_COINGECKO_ENABLED=false  # key gerektirir
WORKER_OXR_ENABLED=false        # key gerektirir
WORKER_TWELVEDATA_ENABLED=false # key gerektirir
```

Fresh-checkout `.env.example` TCMB + EVDS'i açık gönderir (key-free ulusal-veri kaynakları);
key gerektiren kaynaklar kapalıdır — böylece kazara dış API / rate-limit tüketilmez. Bare
binary (env'siz) çalıştırma da güvenlidir: hiçbir worker çalışmaz, `IngestionOrchestrator`
"Hiçbir worker aktif değil" uyarısı verir.

### GeoIP (opsiyonel, F4-7)

`activity_logs` coğrafi zenginleştirmesi MaxMind **GeoLite2-City** ile yapılır. `.mmdb`
**repoya commit edilmez** (lisans); `infrastructure/geoip/README.md`'deki komutla
`GEOIP_ACCOUNT_ID`/`GEOIP_LICENSE_KEY` kullanılarak indirilir. Dosya yoksa GeoIP devre dışı
kalır (`LogWarning` + `country`/`city` null) — **istek başarısız olmaz**. Detay:
[ADR-004](decisions/ADR-004-geoip-distribution.md).

## 5. Testleri Çalıştırma

Lokalde .NET SDK olmadığı için testler **`tests` compose profili** (SDK imajı + repo mount +
compose ağı) ile koşar — `saydin-api` runtime imajı SDK/test projeleri içermez (Faz 3).

```bash
# Tüm solution (unit + integration). Integration için postgres/redis up olmalı.
docker compose up -d postgres redis
docker compose run --rm tests

# Yalnız unit testler (DB gerekmez)
docker compose run --rm tests test tests/Saydin.Api.Tests
docker compose run --rm tests test tests/Saydin.PriceIngestion.Tests

# Gerçek PostgreSQL/Redis entegrasyon testleri (F2.6-21)
docker compose run --rm tests test tests/Saydin.Api.IntegrationTests

# Sadece build doğrulaması (compose'suz, SDK imajı + mount)
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Saydin.Services.sln -c Debug
```

> **Integration testleri (F2.6-21):** Testcontainers BU ortamda kullanılamaz (api
> container'ında docker.sock yok); testler compose ağındaki `postgres`/`redis`'e bağlanır.
> DB/Redis erişilemez veya migration 012 uygulanmamışsa testler `SkippableFact` ile
> **atlanır** (kırmızı olmaz). NuGet önbelleği için `nuget_cache` named volume kullanılır.

## 6. Sık Kullanılan Komutlar

```bash
# Bağımlılıkları yükle
dotnet restore

# Build
dotnet build

# Tüm container'ları durdur (src/Saydin.Services/ dizininden)
docker-compose down

# PostgreSQL'e bağlan
docker exec -it saydin-postgres psql -U saydin -d saydin

# Redis CLI
docker exec -it saydin-redis redis-cli

# Log izle (Aspire Dashboard yerine terminal)
docker logs -f saydin-api 2>&1 | jq .
```

## 7. API Test Örnekleri

```bash
# Sağlık
curl http://localhost:5080/health | jq

# Asset listesi
curl http://localhost:5080/v1/assets | jq

# Tek gün fiyat
curl "http://localhost:5080/v1/assets/USDTRY/price/2020-01-01" | jq

# Fiyat aralığı
curl "http://localhost:5080/v1/assets/USDTRY/price-range?from=2020-01-01&to=2020-12-31" | jq

# "Ya alsaydım" hesaplama
curl -X POST http://localhost:5080/v1/what-if/calculate \
  -H "Content-Type: application/json" \
  -H "X-Device-ID: dev-test-001" \
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
  -H "X-Device-ID: dev-test-001" \
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
  -H "X-Device-ID: dev-test-001" \
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
  -H "X-Device-ID: dev-test-001" \
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
  -H "X-Device-ID: dev-test-001" \
  -d '{
    "type": "what_if",
    "assetSymbol": "USDTRY",
    "title": "USD Test",
    "parameters": {"buyDate": "2020-01-01", "amount": 10000}
  }' | jq

# Senaryoları listele
curl http://localhost:5080/v1/scenarios \
  -H "X-Device-ID: dev-test-001" | jq

# Uygulama konfigürasyonu
curl http://localhost:5080/v1/config \
  -H "X-Device-ID: dev-test-001" | jq

# Prometheus metrikleri
curl http://localhost:5080/metrics
```

## 8. Ortam Değişkenleri

| Değişken | Açıklama | Örnek |
|---|---|---|
| `ConnectionStrings__Postgres` | PostgreSQL bağlantı dizesi | `Host=localhost;...` |
| `ConnectionStrings__Redis` | Redis bağlantı dizesi | `localhost:6379` |
| `Otlp__Endpoint` | OTLP collector endpoint | `http://localhost:4317` |
| `ExternalApis__CoinGecko__ApiKey` | CoinGecko API anahtarı | (key yoksa graceful skip) |
| `ExternalApis__OpenExchangeRates__AppId` | Open Exchange Rates App ID | (key yoksa graceful skip) |
| `ExternalApis__TwelveData__ApiKey` | Twelve Data API anahtarı | (key yoksa graceful skip) |
| `RateLimiting__Enabled` | IP-bazlı rate limiter (F4-5, varsayılan kapalı) | `false` |
| `RateLimiting__PermitLimit` | Pencere başına izin verilen istek | `100` |
| `RateLimiting__WindowSeconds` | Sabit pencere uzunluğu (sn) | `60` |
| `ForwardedHeaders__KnownProxies` | Güvenilen reverse-proxy IP'leri (CSV) | `10.0.0.5` |
| `ForwardedHeaders__KnownNetworks` | Güvenilen subnet'ler (CIDR, CSV) | `10.0.0.0/8` |
| `GeoIp__DatabasePath` | MaxMind GeoLite2 `.mmdb` yolu (opsiyonel) | `/app/geoip/GeoLite2-City.mmdb` |

> **Rate limiting (F4-5 / [ADR-003](decisions/ADR-003-rate-limiting.md)):** IP-bazlı katman
> varsayılan **kapalıdır**. Production'da açmak için `RateLimiting:Enabled=true` ver ve
> reverse-proxy arkasındaysan doğru istemci IP'si için `ForwardedHeaders:KnownProxies` /
> `KnownNetworks`'ü mutlaka yapılandır (aksi halde limiter proxy IP'sine göre partition eder).

> **Güvenlik:** API key'leri asla `appsettings.json`'a yazmayın. Geliştirmede `dotnet user-secrets`,
> production'da environment variable kullanın. Tam strateji (dev/CI/prod katmanları + rotation
> runbook): [ADR-005](decisions/ADR-005-secrets-management.md).

## 9. CI/CD — GitHub Actions

Her `push` ve `pull_request`'te otomatik çalışır:

1. **Build** — `dotnet build`
2. **Test** — `dotnet test`
3. **Docker Build** — `saydin-api` ve `saydin-price-ingestion` image'larını oluşturur

CI pipeline `.github/workflows/` dizininde tanımlıdır.

**Otomatik PR incelemesi (F4-12):** Pull request'ler **CodeRabbit** ile incelenir
(yapılandırma: `.coderabbit.yaml`; `base_branches: main + development`; SQL migration'lar ve
`docs/**` review kapsamındadır). Sourcery kullanılmaz (.NET için anlamlı değil — F1.8-4 ile
kaldırıldı). Ek statik analiz: Codacy (`.codacy.yaml`).

---

## 10. Yaygın Sorunlar

### "Connection refused" — PostgreSQL

```bash
# Container çalışıyor mu?
docker ps | grep saydin-postgres

# Bağlantı testi
docker exec saydin-postgres pg_isready -U saydin
```

### Migration uygulanmamış

```bash
# Tablo var mı?
docker exec saydin-postgres psql -U saydin -d saydin -c "\dt"
# Yoksa migration adımını tekrarla (bkz. Adım 2)
```

### Aspire Dashboard'da trace yok

OTLP bağlantısını doğrula:
```bash
# Dashboard container çalışıyor mu?
docker ps | grep aspire-dashboard

# App'in OTLP'ye bağlandığını log'dan kontrol et
docker logs saydin-api 2>&1 | grep -i otlp
```
