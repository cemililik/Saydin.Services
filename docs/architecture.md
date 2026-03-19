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

## Lokalizasyon (i18n)

API, `Accept-Language` header'ına göre yanıt dilini belirler. `.resx` kaynak dosyaları ve `IStringLocalizer<ErrorMessages>` kullanılır.

**Desteklenen diller:** Türkçe (`tr`, varsayılan), İngilizce (`en`)

**Middleware zinciri:**

```
İstek → ResponseCompression → RequestLocalization → ExceptionHandler → Serilog → Endpoint
```

`RequestLocalizationMiddleware` (`UseRequestLocalization`) `Accept-Language` header'ını parse eder ve `CultureInfo.CurrentUICulture`'ı ayarlar. `ExceptionHandler`'dan önce çalışır — hata yanıtları da lokalize edilir.

**Kaynak dosyaları:**

| Dosya | İçerik |
|-------|--------|
| `Resources/ErrorMessages.resx` | Türkçe hata mesajları + asset isimleri (varsayılan) |
| `Resources/ErrorMessages.en.resx` | İngilizce çeviriler |
| `Resources/ErrorMessages.cs` | `IStringLocalizer<ErrorMessages>` marker sınıfı (`Saydin.Api` namespace) |

**Lokalize edilen alanlar:**
- Exception handler `ProblemDetails.Title` alanları
- `EndpointExtensions` DeviceId doğrulama mesajları
- `WhatIfCalculator` / `SavedScenarioService` validasyon mesajları
- Asset display name'ler (`Asset_{Symbol}` convention'ı ile, fallback: DB'deki `display_name`)

**Cache dil ayrımı:** `assets:info` ve `whatif` cache key'lerine dil kodu eklenir (ör. `assets:info:27:en`). Farklı dillerdeki istekler birbirinin cache'ini bozamaz.

**Yeni asset eklendiğinde:** Her iki `.resx` dosyasına `Asset_{SYMBOL}` key'i ile çeviri eklenir. Key bulunamazsa DB'deki `display_name` fallback olarak kullanılır.

## DailyLimitGuard (Günlük Kullanım Limiti)

Günlük kullanım kotası kontrolü `DailyLimitGuard` servisi tarafından merkezi olarak yönetilir. Her hesaplama servisi (WhatIfCalculator, DcaCalculator) kendi `usageKeyPrefix` değeriyle bu guard'ı çağırır:

```
usage:whatif:{userId}:{yyyy-MM-dd}   → WhatIfCalculator
usage:dca:{userId}:{yyyy-MM-dd}      → DcaCalculator
```

`GetLimitAndKey` helper metodu ortak kontrol mantığını çıkarır:
- Premium kullanıcılar → bypass (ne check ne increment)
- Limit = 0 → unlimited tier, bypass
- Diğerleri → Redis key oluştur, limit kontrol et

**Fail-open prensibi:** Redis erişilemezse kullanıcı engellemez — hata loglanır, istek devam eder.

## Feature Flags (Özellik Bayrakları)

Her plan tier'ı (`free`/`premium`) `FeatureOptions` ile hangi özelliklerin aktif olduğunu belirler:

| Bayrak | Varsayılan | Etkisi |
|---|---|---|
| `Comparison` | `true` | Compare endpoint erişimi |
| `InflationAdjustment` | `true` | Enflasyon düzeltmeli hesaplama |
| `Share` | `true` | Paylaşım özelliği |
| `Dca` | `true` | DCA hesaplama erişimi |
| `PriceHistoryMonths` | `12` | Fiyat geçmişi ay sınırı |

Devre dışı özellik çağrılırsa `InvalidOperationException("FeatureDisabled")` fırlatılır ve `IStringLocalizer` ile lokalize edilir.

Feature flag'lar `/v1/config` endpoint'inden istemciye döner — UI dinamik olarak kısıtlama uygular.

## Senaryo Tipi Normalizasyonu

`SavedScenarioService` senaryo kaydetme sırasında `Type` alanını `ToLowerInvariant()` ile normalize eder. İzin verilen tipler:

```
what_if | comparison | portfolio | dca
```

- `what_if` ve `dca` tipleri geçerli bir asset sembolü gerektirir (FK kontrolü)
- `comparison` ve `portfolio` tipleri asset doğrulamasını atlar

## Exception Handling Zinciri

```
İstek → Endpoint → Service → Exception fırlatıldı
                                    │
         ┌──────────────────────────┴────────────────────────────┐
         ▼                                                         ▼
PriceNotFoundExceptionHandler                           GlobalExceptionHandler
(404 + ProblemDetails)                                  (500 + ProblemDetails + traceId)
```

Her domain exception için ayrı `IExceptionHandler` yazılır ve zincire eklenir. Tüm handler'lar `IStringLocalizer<ErrorMessages>` inject ederek `Title` alanını lokalize eder.

## Cache Stratejisi (Redis)

```
price:{symbol}:{date}                  → TTL 24 saat   (tek gün fiyatı)
prices:{symbol}:{from}:{to}            → TTL 1 saat    (tarih aralığı)
whatif:v3:{symbol}:{buy}:{sell}:...:{lang} → TTL 1 saat (hesaplama sonucu; v3: lokalize displayName)
assets:sig                             → TTL 5 dakika  (aktif asset sayısı — imza)
assets:list:{count}                    → TTL 6 saat    (tüm asset listesi — sadece temel alanlar)
assets:info:{sig}:{lang}              → TTL 1 saat    (zenginleştirilmiş liste, dil bazlı cache)
```

Cache anahtarı normalize edilmiş parametrelerle oluşturulur.

**`whatif` cache versiyonlama:** Lokalize `assetDisplayName` eklendikten sonra anahtar `whatif:v3:...:lang` olarak güncellendi. Dil kodu (`tr`/`en`) cache key'in parçasıdır — farklı dillerdeki istekler ayrı cache'lenir.

**Asset listesi cache stratejisi:**
- `assets:sig` — aktif asset sayısını tutar (5 dk TTL). İmza değeri değiştiğinde `assets:list` ve `assets:info` otomatik yenilenir.
- `assets:list:{sig}` — temel asset listesi (6 saat TTL). Sadece sembol/isim/kategori alanları.
- `assets:info:{sig}:{lang}` — `firstPriceDate`/`lastPriceDate` dahil zenginleştirilmiş liste (1 saat TTL, dil bazlı). Flutter tarih picker aralığı için kullanılır. Lokalize `displayName` içerdiği için dil kodu cache key'e dahildir.

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
