# Saydin.Services — Mimari

## Servis Haritası

```
┌─────────────────────────────────────────────────────────┐
│                    Flutter Client                        │
│                  (Saydin.Client)                         │
└────────────────────────┬────────────────────────────────┘
                         │ HTTP (REST)
                         ▼
┌─────────────────────────────────────────────────────────┐
│                    Saydin.Api                            │
│              (.NET 10 Minimal API)                       │
│  Endpoints/ → Services/ → Repositories/ → PostgreSQL    │
└────────────────────────────────────────────────────────┘
                         │ PostgreSQL (shared DB)
┌───────────────────────────────────────────────────────────┐
│                Saydin.PriceIngestion                      │
│              (.NET 10 Background Worker)                  │
│  IngestionOrchestrator → Adapters → Mappers → PostgreSQL  │
└───────────────────────────────────────────────────────────┘

         Ortak: Saydin.Shared (entity, exception, diagnostics)
```

## Katman Kuralları

### Saydin.Api — İç Katmanlar

```
Endpoints (IEndpointRouteBuilder extension methods)
    │
    ▼
Services (IWhatIfCalculator, IAssetService)
    │
    ▼
Repositories (IPriceRepository, IAssetRepository)
    │
    ▼
PostgreSQL + Redis
```

- **Endpoints:** Route tanımları, request validation, response şekillendirme. İş mantığı yok.
- **Services:** "Ya alsaydım" hesaplama motoru, cache yönetimi. Dış I/O doğrudan yok.
- **Repositories:** Veri erişimi (Entity Framework Core LINQ). Sadece I/O, iş mantığı yok.

### Saydin.PriceIngestion — İç Katmanlar

```
IngestionOrchestrator (BackgroundService)
    │
    ▼
Workers (TcmbWorker, CoinGeckoWorker, ...)
    │
    ▼
Adapters (IExternalPriceAdapter implementasyonları)
    │
    ▼
Mappers (Ham API yanıtı → PricePoint)
    │
    ▼
PostgreSQL (price_points tablosu, UPSERT)
```

## Dış Veri Kaynakları

| Adapter | API | Asset | Zamanlama |
|---|---|---|---|
| `TcmbAdapter` | TCMB XML | USD/TRY, EUR/TRY, GBP/TRY, CHF/TRY vb. | 16:30 Türkiye (piyasa kapanışı) |
| `CoinGeckoAdapter` | CoinGecko API | BTC, ETH, BNB, XRP | 06:00 UTC |
| `OpenExchangeRatesAdapter` | Open Exchange Rates | XAU/TRY (altın), XAG/TRY (gümüş) — gram bazında | 22:00 UTC |
| `TwelveDataAdapter` | Twelve Data | THYAO, GARAN | 19:00 Türkiye (BIST kapanışı) |

**Not:** `OpenExchangeRatesAdapter` USD-base yanıtındaki XAU/XAG oranlarını
`(1 / metalRate) * tryRate / 31.1034768` formülüyle gram/TRY'ye çevirir.
Aynı tarih için XAU ve XAG tek HTTP isteğiyle alınır (day-level in-memory cache).

## Servis Sınırları (KESIN KURAL)

```
Saydin.Api           ─── TCMB, CoinGecko vb. API'lere istek ATMAZ
Saydin.PriceIngestion ── HTTP endpoint EXPOSE ETMEZ
Saydin.Api ↔ Saydin.PriceIngestion arası iletişim: sadece PostgreSQL
```

Bu kural kasıtlı olarak uygulanır. Bir servisin diğerini doğrudan çağırması gerekiyorsa, bu mimari karar gözden geçirilmelidir (bkz. ADR-001).

## Resilience Katmanı

Her `IExternalPriceAdapter` implementasyonu şu Polly pipeline'ını zorunlu olarak kullanır:

```
Request → Timeout(30s) → Retry(3, exponential) → CircuitBreaker(5 fail → open) → Adapter
```

`Microsoft.Extensions.Http.Resilience` paketi ile `IHttpClientFactory` üzerinden yapılandırılır.

## Veritabanı Erişim Deseni

```sql
-- price_points tablosuna her zaman UPSERT:
INSERT INTO price_points (asset_id, price_date, close, ...)
VALUES (@assetId, @date, @close, ...)
ON CONFLICT (asset_id, price_date) DO UPDATE
  SET close = EXCLUDED.close,
      updated_at = NOW();
```

`float`/`double` yasak. Tüm fiyat değerleri: `decimal` (C#) / `NUMERIC(18,6)` (PostgreSQL).

## Exception Handling Zinciri

```
İstek → Endpoint → Service → Exception fırlatıldı
                                    │
         ┌──────────────────────────┴────────────────────────────┐
         ▼                                                         ▼
PriceNotFoundExceptionHandler                           GlobalExceptionHandler
(404 + ProblemDetails)                                  (500 + ProblemDetails + traceId)
```

Her domain exception için ayrı `IExceptionHandler` yazılır ve zincire eklenir.

## Cache Stratejisi (Redis)

```
price:{symbol}:{date}              → TTL 24 saat   (tek gün fiyatı)
prices:{symbol}:{from}:{to}        → TTL 1 saat    (tarih aralığı)
whatif:{symbol}:{buy}:{sell}:...   → TTL 1 saat    (hesaplama sonucu)
assets:sig                         → TTL 5 dakika  (aktif asset sayısı — imza)
assets:list:{count}                → TTL 6 saat    (tüm asset listesi)
```

Cache anahtarı normalize edilmiş parametrelerle oluşturulur.

**Asset listesi cache invalidation:** `assets:sig` anahtarı aktif asset sayısını tutar (5 dk TTL). Her istekte `sig` ile hesaplanan `assets:list:{sig}` anahtarı aranır. Yeni asset eklendiğinde `sig` süresi dolduğunda otomatik yenilenir — manuel Redis flush gerekmez.

## Observability

- **Structured logging:** Serilog → Console (JSON) + OTLP sink → Aspire Dashboard
- **Tracing:** OpenTelemetry → OTLP → Aspire Dashboard (her iş akışı adımı custom Activity ile izlenir)
- **Metrics:** OpenTelemetry + Prometheus scrape (`/metrics`)
- **Health checks:** `/health` → PostgreSQL + Redis bağlantı kontrolleri

Detaylar: [../../docs/architecture/observability.md](../../docs/architecture/observability.md)

## Proje Referans Kuralları

```
Saydin.Api      → Saydin.Shared  ✓
Saydin.Api      → Saydin.PriceIngestion  ✗ YASAK
Saydin.PriceIngestion → Saydin.Shared  ✓
Saydin.PriceIngestion → Saydin.Api  ✗ YASAK
Saydin.Shared   → herhangi biri  ✗ YASAK (shared is leaf)
```
