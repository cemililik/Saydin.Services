# Saydin.Services

.NET 10 backend servisleri — finansal "ya alsaydım?" hesaplama motoru.

## Servisler

| Servis | Açıklama | Port |
|---|---|---|
| `Saydin.Api` | Flutter uygulamasına Minimal API sunar | 5000 |
| `Saydin.PriceIngestion` | Dış finansal API'lerden fiyat verisi çeker | — |
| `Saydin.Shared` | Ortak entity, exception, diagnostics | — |

## Hızlı Başlangıç

### Ön koşullar

- Docker + Docker Compose (backend servisleri için)
- .NET 10 SDK (yerel geliştirme için, opsiyonel)

### Altyapıyı Başlat

```bash
# Proje kökünden (Saydın/)
docker-compose up -d

# Veritabanı migration'ını uygula
docker exec -i saydin-postgres psql -U saydin -d saydin \
  < src/Saydin.Services/infrastructure/postgres/migrations/001_initial.sql
```

### Uygulamayı Çalıştır (Docker)

```bash
cd src/Saydin.Services
docker-compose up -d  # Saydin.Api
```

### Uygulamayı Çalıştır (Yerel .NET)

```bash
cd src/Saydin.Services

# User secrets ile bağlantı dizelerini ayarla
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Database=saydin;Username=saydin;Password=saydin_pass" \
  --project src/Saydin.Api

dotnet run --project src/Saydin.Api
```

### API Test

```bash
# Sağlık kontrolü
curl http://localhost:5000/health

# Asset listesi
curl http://localhost:5000/v1/assets

# "Ya alsaydım" hesaplama
curl -X POST http://localhost:5000/v1/what-if/calculate \
  -H "Content-Type: application/json" \
  -H "X-Device-ID: test-device-123" \
  -d '{
    "assetSymbol": "USDTRY",
    "buyDate": "2020-01-01",
    "sellDate": "2024-01-01",
    "amount": 10000,
    "amountType": "TRY"
  }'
```

## Mimari

```
Saydin.Api (HTTP)          Saydin.PriceIngestion (Worker)
     │                              │
     │         PostgreSQL           │
     └──────────────────────────────┘
                    │
              Saydin.Shared
```

**Temel kural:** `Saydin.Api` hiçbir dış finansal API'ye istek atmaz. `Saydin.PriceIngestion` hiçbir HTTP endpoint expose etmez. Servisler sadece veritabanı üzerinden haberleşir.

Detaylı mimari: [docs/architecture.md](docs/architecture.md)

## Geliştirme

Yerel geliştirme kurulumu, user-secrets ve Docker iş akışı için: [docs/development-guide.md](docs/development-guide.md)

## Observability

Altyapı ayağa kalktıktan sonra:

| Araç | URL | Kullanım |
|---|---|---|
| Aspire Dashboard | http://localhost:18888 | Traces, logs, metrics |
| pgAdmin | http://localhost:5050 | PostgreSQL yönetimi |
| Redis Insight | http://localhost:5540 | Redis izleme |
| Prometheus | http://localhost:9090 | Metrik sorgulama |
| Swagger UI | http://localhost:5000/openapi/v1 | API dökümantasyonu |

## Mimari Kurallar

Agent dosyası: [CLAUDE.md](CLAUDE.md)
