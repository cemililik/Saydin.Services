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

### EF Core ile Yeni Migration Ekleme

```bash
cd src/Saydin.Services

# Yeni migration oluştur (Saydin.Shared projesine, Saydin.Api startup projesi olarak)
dotnet ef migrations add <MigrationAdı> \
  --project src/Saydin.Shared \
  --startup-project src/Saydin.Api

# Veritabanını güncelle
dotnet ef database update \
  --project src/Saydin.Shared \
  --startup-project src/Saydin.Api

# Migration listesi
dotnet ef migrations list \
  --project src/Saydin.Shared \
  --startup-project src/Saydin.Api
```

> **Not:** EF Core migrations için `Microsoft.EntityFrameworkCore.Design` paketi `Saydin.Api.csproj`'da mevcut.

## 3. Saydin.Api Çalıştırma

### Docker ile

```bash
cd src/Saydin.Services
docker build -f src/Saydin.Api/Dockerfile -t saydin-api .
docker run -p 5080:8080 \
  -e ConnectionStrings__Postgres="Host=host.docker.internal;Database=saydin;Username=saydin;Password=saydin_pass" \
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
  "Host=localhost;Database=saydin;Username=saydin;Password=saydin_pass" \
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
  "Host=localhost;Database=saydin;Username=saydin;Password=saydin_pass" \
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

## 5. Testleri Çalıştırma

```bash
cd src/Saydin.Services

# Tüm testler
dotnet test

# Belirli proje
dotnet test tests/Saydin.Api.Tests/

# Coverage raporu
dotnet test --collect:"XPlat Code Coverage"
```

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

> **Güvenlik:** API key'leri asla `appsettings.json`'a yazmayın. Geliştirmede `dotnet user-secrets`, production'da environment variable kullanın.

## 9. CI/CD — GitHub Actions

Her `push` ve `pull_request`'te otomatik çalışır:

1. **Build** — `dotnet build`
2. **Test** — `dotnet test`
3. **Docker Build** — `saydin-api` ve `saydin-price-ingestion` image'larını oluşturur

CI pipeline `.github/workflows/` dizininde tanımlıdır.

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
