# Saydın Services — Agent Kuralları

## Proje Bağlamı

Saydın, Türk kullanıcılara yönelik finansal "ya alsaydım?" hesaplama uygulamasının backend'idir.

Bu repo iki uzun ömürlü .NET 10 servisini, one-shot database control-plane araçlarını, offline
calendar aracını ve ortak kütüphaneleri içerir:
- `Saydin.Api` — Flutter uygulamasına HTTP endpoint'leri sunan Minimal API servisi
- `Saydin.PriceIngestion` — Dış finansal API'lerden fiyat verisi çeken background worker
- `Saydin.DatabaseMigrator` — SQL migration control-plane; servislerden önce tek sefer çalışır
- `Saydin.DatabaseRoleBootstrap` / `Saydin.DatabaseSecurity` — managed rol/secret sınırı
- `Saydin.DataQualityAudit` / `Saydin.DataRepair` — salt-okunur kanıt ve imzalı operatör onarımı
- `Saydin.CalendarData` — offline authoritative calendar acquisition/verification aracı
- `Saydin.Shared` — Her iki servisin kullandığı ortak entity, exception ve extension sınıfları

---

## Geliştirme Ortamı Kuralı (KRİTİK)

**Lokal makinede .NET 10 SDK kurulu değildir.**
`dotnet build`, `dotnet test`, `dotnet run` gibi komutları **doğrudan çalıştırma.**
Tüm build, test ve çalıştırma işlemleri **Docker Compose** üzerinden yapılır:

```bash
# İlk checkout'ta (ve backup login yenilemesi gerektiğinde) private secret'ları
# ve Compose'un zorunlu runtime metadata dosyasını üret.
./infrastructure/secrets/bootstrap-dev-database.sh

# Kod değişikliğinden sonra image'ı yeniden oluştur ve servisleri başlat
docker compose --env-file .env --env-file .env.database-runtime build
docker compose --env-file .env --env-file .env.database-runtime up -d

# Lokal unit kapısı (`test` compose profili — pinned SDK imajı + repo mount).
# `api`/`saydin-api` runtime imajı SDK ve test projeleri içermez; `dotnet test` ÇALIŞMAZ.
docker compose --env-file .env --env-file .env.database-runtime --profile test run --rm tests
docker compose --env-file .env --env-file .env.database-runtime --profile test run --rm tests test tests/Saydin.Api.Tests
docker compose --env-file .env --env-file .env.database-runtime run --rm database-migrator --verify-only
# Root `tests` servisi purpose-specific DB credential taşımaz ve solution/integration komutlarını
# fail-closed reddeder. Gerçek PostgreSQL/Redis kabulü: `.github/workflows/ci.yml` integration-test
# job'ı + `.github/compose.integration.yml` (UUID DB/project/volume, no host ports, zero-skip gate).
```

**`docker compose run --rm api dotnet build` KULLANMA** — build ve deploy için her zaman `docker compose build && docker compose up -d` kullan.

Lokal `dotnet` bulunamadı diye debelenme — her zaman Docker Compose kullan.

**NOT (C-02):** Migration zinciri artık Postgres `initdb.d` tarafından değil, her deploy/startup'ta
pre-bootstrap → one-shot `database-migrator` → post-migration bootstrap sırasıyla çalıştırılır.
PostgreSQL health yalnız bağlantıyı ölçer; API, ingestion ve DB monitoring
`service_completed_successfully` ile doğrulanmış şema ve post-022 backup rol grafiğini bekler.
Bir migration hata verirse job non-zero döner ve downstream başlamaz. TimescaleDB hypertable'larında (`activity_logs`)
compression **enabled** iken `ALTER COLUMN ... TYPE` yasaktır (TS 2.16.1). Bu nedenle compression'ı
etkileyen kolon değişiklikleri için `008b` (009'dan önce disable) / `013` (012'den sonra re-enable)
sarmalama deseni kullanılır — yeni `ALTER COLUMN TYPE` eklerken bu pencereyi koru.

---

## Cache Kuralı (KRİTİK)

**Cache ile ilgili herhangi bir işlem yapmadan önce `docs/cache-strategy.md` dosyasını oku.**
Cache key ekleme, TTL değiştirme, limit mantığı güncelleme veya Redis kullanımı değiştirme
durumlarında işlem sonrası bu dokümanı güncelle.

---

## Commit Kuralı (KRİTİK)

**Kod değişikliklerini commit etmeden önce mutlaka build ve testleri çalıştır.**

```bash
# Build doğrulaması (global.json ile aynı exact SDK digest'i + repo mount):
docker run --rm -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c \
  dotnet build Saydin.Services.sln -c Debug
# Unit + coverage ratchet (`test` compose profili):
docker compose --env-file .env --env-file .env.database-runtime --profile test run --rm tests
```

Build veya test başarısız olursa commit atma, önce hatayı düzelt.

---

## Mimari Kurallar (KESINLIKLE UYULACAK)

### Teknoloji

- **Target framework:** `net10.0`
- **API yaklaşımı:** Minimal API — `Controller` sınıfı YASAKTIR
- **Endpoint organizasyonu:** `Endpoints/` klasöründe extension method'lar (`IEndpointRouteBuilder`)
- **OpenAPI:** .NET 10 native `Microsoft.AspNetCore.OpenApi` paketi
- **ORM:** Entity Framework Core (`Npgsql.EntityFrameworkCore.PostgreSQL`) — Dapper YASAKTIR
- **DbContext:** `SaydinDbContext` `Saydin.Shared/Data/` içinde yaşar, her iki servis tarafından paylaşılır
- **Saydin.Api:** `AddDbContext<SaydinDbContext>()` → scoped lifetime
- **Saydin.PriceIngestion:** `AddDbContextFactory<SaydinDbContext>()` → singleton-safe factory pattern
- **Migration:** `infrastructure/postgres/migrations/` altındaki immutable sıralı SQL + `Saydin.DatabaseMigrator`; EF migration kullanılmaz
- **HTTP Client:** `IHttpClientFactory` ile kayıtlı named client'lar — `new HttpClient()` YASAKTIR.
  Tek kapsamlı istisna, hosted service olmayan `tools/calendar-data` one-shot control-plane
  composition root'udur; bounded stdout/stderr ve hardened handler sözleşmesi
  `infrastructure/calendar/README.md` içinde tanımlanır.

### Servis Sınırları

- `Saydin.Api` hiçbir dış finansal API'ye (TCMB, CoinGecko, OpenExchangeRates, Twelve Data, EVDS) HTTP isteği ATMAZ
- `Saydin.PriceIngestion` hiçbir HTTP endpoint EXPOSE ETMEZ (`Microsoft.AspNetCore` referansı yasak)
- Servisler arasındaki iletişim **yalnızca PostgreSQL veritabanı** üzerinden gerçekleşir
- Ortak tipler `Saydin.Shared`'de yaşar; servisler birbirini referans almaz

### Katman Kuralları

```
Endpoints  →  Services  →  Repositories  →  Database
```
- Endpoint handler'lar Service çağırır, Repository'ye doğrudan erişmez
- Service'ler Repository çağırır, DbContext'e doğrudan erişmez
- İş mantığı Endpoint handler'larda YOK
- Dar güvenlik istisnası: installation credential lifecycle (migration 021–024)
  sabit-zamanlı verifier resolve/rehash ve SQLSTATE→401 sözleşmesi nedeniyle
  `InstallationEndpoints` → `IInstallationRepository` doğrudan yolunu kullanır;
  `InstallationRepository` yalnız DI ile paylaşılan `NpgsqlDataSource` ve parametreli
  SECURITY DEFINER fonksiyon çağrıları kullanabilir. Bu istisna başka endpoint/repository
  akışlarına genişletilemez.

### DTO Kuralları

- Tüm request/response DTO'ları `record` type olarak tanımlanır (immutability)
- DTO'lar `Models/Requests/` ve `Models/Responses/` klasörlerinde ayrı tutulur
- Domain entity'leri (Shared) DTO olarak kullanılmaz

---

## Kod Standartları

### Finansal Hassasiyet (KRİTİK)

```csharp
// DOĞRU ✓
decimal price = 23.45m;
decimal result = Math.Round(price * quantity, 2, MidpointRounding.AwayFromZero);

// YANLIŞ ✗ — KESINLIKLE YASAK
double price = 23.45;
float amount = 10000f;
```

`price`, `amount`, `value`, `rate`, `profit`, `loss`, `quantity` adını içeren **tüm değişkenler** `decimal` tipinde olmalıdır.

### İsimlendirme

```csharp
// Interface: I prefix
public interface IWhatIfCalculator { }

// Async method: Async suffix
public Task<WhatIfResult> CalculateAsync(WhatIfRequest request, CancellationToken ct);

// Private field: _ prefix
private readonly IPriceRepository _priceRepository;

// Record DTO
public record WhatIfRequest(string AssetSymbol, DateOnly BuyDate, decimal Amount);
```

### Async Kuralları

```csharp
// YANLIŞ ✗ — deadlock riski
var result = service.CalculateAsync().Result;
service.CalculateAsync().Wait();

// YANLIŞ ✗ — exception yakalanmaz
async void DoSomething() { }  // Event handler dışında yasak

// DOĞRU ✓
var result = await service.CalculateAsync(ct);
```

### Hata Yönetimi

- Tüm dış API çağrıları try/catch ile sarılır
- Exception sessizce yutulmaz — minimum `ILogger` ile loglanır
- Kullanıcıya dönecek hata mesajları `IStringLocalizer<ErrorMessages>` ile lokalize edilir (`Accept-Language` header'a göre)
- `ProblemDetails` formatı kullanılır (RFC 7807)

### Güvenlik

- API key'ler asla `appsettings.json`'a yazılmaz → environment variable veya user-secrets
- SQL'de string interpolation YASAKTIR:
  ```csharp
  // YANLIŞ ✗
  $"SELECT * FROM price_points WHERE symbol = '{symbol}'"

  // DOĞRU ✓
  "SELECT * FROM price_points WHERE asset_id = @assetId"
  ```
- Dış API isteklerinde timeout zorunludur

### Zaman (TimeProvider) — Faz 3

- API servislerinde `DateTime.UtcNow` / `DateTimeOffset.UtcNow` **doğrudan kullanma** —
  constructor'a enjekte edilen `TimeProvider` üzerinden `timeProvider.GetUtcNow()` kullan.
  `TimeProvider.System` singleton kayıtlıdır (Program.cs). Testlerde
  `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` ile saat dondurulur
  (gün-dönümü flaky'liği önlenir).
- PriceIngestion worker'ları şimdilik kapsam dışı (worker zamanlaması test determinizmi gerektirmiyor).

### İstek Bağlamı (IInstallationPrincipalContext)

- Korumalı route'lar server-issued `Authorization: Installation <token>` credential'ını
  doğrular; aktif principal id scoped `IInstallationPrincipalContext` içine yazılır.
- İş service'i arayüzleri (`IWhatIfCalculator`, `IDcaCalculator`, `ISavedScenarioService`,
  `IAppConfigService`) client-chosen device id taşımaz. `X-Device-ID` authorization veya kota
  kökü değildir.
- `IInstallationPrincipalContext` **scoped** kayıtlıdır; singleton'a enjekte ETME
  (principal'lar arası sızıntı). Dağıtık quota/limiter anahtarları ham principal/IP değil,
  purpose-separated HMAC pseudonym kullanır.

### Kod Stili (.editorconfig) — Faz 3

- Kök `.editorconfig` isimlendirme (I-prefix interface, `_camelCase` private field) + format
  kurallarını **öneri (suggestion)** seviyesinde tutar. `EnforceCodeStyleInBuild` set EDİLMEDİ →
  build'i kırmaz. XML doc comment zorunluluğu (CS1591) bilinçli olarak kapalıdır.

---

## Observability Kuralları

### Logging (Serilog)

```csharp
// DOĞRU ✓ — parametreli, structured log
_logger.LogInformation("Fiyat hesaplandı: {Symbol} {BuyDate} → {ProfitPercent}%",
    symbol, buyDate, profitPercent);

// YANLIŞ ✗ — string interpolation (structured değil, query yapılamaz)
_logger.LogInformation($"Fiyat hesaplandı: {symbol} {buyDate} → {profitPercent}%");
```

Log seviyesi kuralları:
- `LogError` → beklenmeyen exception, dış API tamamen başarısız
- `LogWarning` → beklenen ama anormal durum (fiyat bulunamadı, rate limit 429)
- `LogInformation` → iş akışı adımları (ingestion başladı/bitti, hesaplama yapıldı)
- `LogDebug` → yalnızca Development ortamında, detay bilgi

### Exception Handling (IExceptionHandler Zinciri)

```
RequestBodyTooLargeExceptionHandler → ValidationExceptionHandler
  → FeatureDisabledExceptionHandler → PriceNotFoundExceptionHandler
  → AssetNotFoundExceptionHandler → ScenarioNotFoundExceptionHandler
  → ScenarioLimitExceededExceptionHandler → DailyLimitExceededExceptionHandler
  → QuotaUnavailableExceptionHandler → ExternalApiExceptionHandler → GlobalExceptionHandler
```

(Kayıt sırası `Program.cs`'tedir; spesifik handler'lar önce, `GlobalExceptionHandler` her zaman
en sonda. HTTP kodları: 400 · 403 · 404 · 413 · 422 · 429 · 502 · 503 · 500.)

**Her domain exception için ayrı `IExceptionHandler` sınıfı yazılır.**

```csharp
// Saydin.Api/Exceptions/{ExceptionType}Handler.cs
public sealed class PriceNotFoundExceptionHandler(ILogger<...> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not PriceNotFoundException ex) return false;

        logger.LogWarning(ex, "Fiyat bulunamadı: {Symbol} / {Date}", ex.AssetSymbol, ex.Date);

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/price-not-found",
            Title = "Fiyat bulunamadı",
            Status = StatusCodes.Status404NotFound,
            Detail = ex.Message,
            Extensions = { ["traceId"] = Activity.Current?.TraceId.ToString() }
        }, ct);

        return true;
    }
}
```

Kurallar:
- `GlobalExceptionHandler` her zaman zincirin **sonunda** kayıtlıdır
- `GlobalExceptionHandler` her 5xx yanıtında `traceId` döner
- Exception'ı yutan catch block YASAK
- `ProblemDetails` formatı zorunludur (RFC 7807)

### Tracing (OpenTelemetry)

```csharp
// DOĞRU ✓ — iş mantığı adımlarına custom span ekle
using var activity = SaydinActivitySource.Instance.StartActivity("WhatIfCalculation");
activity?.SetTag("asset.symbol", request.AssetSymbol);
activity?.SetTag("buy.date", request.BuyDate.ToString());
```

- `SaydinActivitySource` → `Saydin.Shared/Diagnostics/SaydinActivitySource.cs`'de merkezi tanımlanır
- Health check endpoint'leri trace'e dahil edilmez (gürültü)
- Dış API adapter'ları otomatik olarak `AddHttpClientInstrumentation()` ile izlenir

### Metrics (OpenTelemetry + Prometheus)

- İş metrikleri `Saydin.Shared/Diagnostics/SaydinMetrics.cs`'de merkezi tanımlanır
- `GET /metrics` endpoint'i Prometheus tarafından kazınır
- Özel metrik eklenirken `Meter` ve `Counter/Histogram` kullan, ham sayı tutma

### Health Checks

```csharp
// Program.cs — PostgreSQL paylaşılan NpgsqlDataSource üzerinden manuel async kontrol
// (SELECT 1); Redis için AddRedis. AspNetCore.HealthChecks.Npgsql AddNpgSql KULLANILMAZ.
builder.Services.AddHealthChecks()
    .AddAsyncCheck("postgresql", async ct =>
    {
        await using var conn = await npgsqlDataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return HealthCheckResult.Healthy();
    }, tags: ["db"])
    .AddRedis(redisConnectionString, name: "redis", tags: ["cache"]);

app.MapHealthChecks("/health");
```

---

## Dış API Adaptörleri

Her adaptör şu kuralları izler:

```csharp
public interface IExternalPriceAdapter
{
    string Source { get; }   // "tcmb", "coingecko", "openexchangerates", "twelvedata"

    Task<AdapterOutcome<PricePoint>> FetchRangeAsync(
        PriceFetchRequest request,
        CancellationToken ct);
}
// EVDS (enflasyon) ayrı `IInflationAdapter`'ı uygular — price adapter sözleşmesini değil.
```

- `Microsoft.Extensions.Http.Resilience` (Polly v8) custom `AddResilienceHandler` ile merkezi pipeline (`HttpResilienceExtensions.AddSaydinResilience`)
- **retry** (3 deneme, exponential backoff + jitter)
- **circuit breaker** — `MinimumThroughput=2`, `FailureRatio=1.0`, `SamplingDuration=120s` → düşük-trafik worker'larda (örn. EVDS aylık) pratikte ~2 ardışık hatada devre 120 sn açılır (rationale: `HttpResilienceExtensions` içi yorum)
- Her denemede 30 saniye AttemptTimeout (send/header acquisition); retry zincirinin tamamında 3 dk TotalRequestTimeout. `ResponseHeadersRead` sonrası body bu attempt timer'ının dışında olduğundan worker aynı 3 dk sabitini adapter'ın header + body + parse + lease-renewal akışına mutlak bütçe olarak uygular; aşım durable `provider_deadline` retryable outcome üretir
- 429'da gecikme `max(exponential+jitter, bounded Retry-After)` olur; dış 3 dk bütçeyi aşamaz

---

## Veritabanı Kuralları

### Entity Framework Core

```csharp
// DOĞRU ✓ — LINQ ile sorgu
var price = await context.PricePoints
    .Where(pp => pp.Asset.Symbol == symbol && pp.PriceDate == date)
    .FirstOrDefaultAsync(ct);

// YANLIŞ ✗ — Raw SQL string interpolation (injection riski)
var price = await context.Database.ExecuteSqlRawAsync($"SELECT * WHERE symbol = '{symbol}'");

// DOĞRU ✓ — UPSERT için ExecuteSqlInterpolatedAsync (parametreli, güvenli)
await context.Database.ExecuteSqlInterpolatedAsync(
    $"INSERT INTO price_points (...) VALUES ({assetId}, {date}, ...) ON CONFLICT DO UPDATE ...", ct);
```

- `price_points` tablosuna **her zaman UPSERT** kullanılır (`ON CONFLICT DO UPDATE`)
- `SaydinDbContext` `Saydin.Shared/Data/` altında merkezi tanımlanır
- Entity konfigürasyonları `Saydin.Shared/Data/Configurations/` altında `IEntityTypeConfiguration<T>` ile yapılır
- PostgreSQL enum tipi (`asset_category`) EF Core ile `HasPostgresEnum<AssetCategory>()` ve `MapEnum<AssetCategory>()` üzerinden yönetilir — TypeHandler yazmak YASAKTIR
- Migration dosyaları `infrastructure/postgres/migrations/` altında numaralandırılır
- Mevcut migration dosyaları **asla değiştirilmez** — yeni migration eklenir
- `ingestion_jobs` tablosuna başarı ve hata durumları yazılır

### Migration Komutları

```bash
# Yeni production migration: sıradaki numarayla yeni .sql ekle; mevcut 001..024'ü değiştirme.
# Mevcut hedefe pending migration'ları uygula (Compose secrets env'den gelir):
docker compose --env-file .env --env-file .env.database-runtime run --rm database-migrator

# Uygulama yapmadan checksum, control state ve core şema fingerprint doğrula:
docker compose --env-file .env --env-file .env.database-runtime run --rm database-migrator --verify-only
```

---

## Test Kuralları

- `Services/` katmanındaki her public method için unit test zorunludur
- Test adlandırma: `MethodName_Scenario_ExpectedResult`
- Dış adaptörler için en az deserializasyon testi (contract test) gerekir
- **Test türü → izolasyon stratejisi (mock politikası) — F4-9:**
  - **Service unit testleri:** Collaborator bağımlılıkları (repository, cache,
    `IDailyLimitGuard`, `IStringLocalizer`, `IInstallationPrincipalContext`, dış adapter interface'leri)
    **NSubstitute ile mock'lanır** — serbesttir. Determinizm için saat `FakeTimeProvider`
    ile dondurulur. Hedef: iş mantığını DB/Redis olmadan hızlı ve izole test etmek.
  - **Dış adapter contract testleri:** Yalnız HTTP katmanı sahtelenir
    (`HttpMessageHandler` / fake handler ile sabit yanıt); deserializasyon, retry/backoff,
    timeout ve hata→exception davranışı doğrulanır.
  - **Veritabanı / integration testleri:** **mock YASAK** — gerçek PostgreSQL ve Redis kullanılır.
    Root `tests` profili integration çalıştırmaz; kanonik required akış
    `.github/compose.integration.yml` üzerinde purpose-specific login/secret ve ephemeral DB'lerle
    fail-fast çalışır. Her TRX'te `total=executed=passed`, failed/skipped/notExecuted sıfırdır.

---

## Yeni Özellik Ekleme

### Yeni Asset Eklemek
`.claude/commands/add-asset.md` dosyasındaki 8 adımlı checklist'i uygula.
Ek olarak: `Resources/ErrorMessages.resx` ve `Resources/ErrorMessages.en.resx` dosyalarına `Asset_{SYMBOL}` key'i ile Türkçe/İngilizce display name ekle.

### Yeni Endpoint Eklemek
1. `Endpoints/` klasöründe ilgili extension method'a ekle
2. Request/Response record type'larını `Models/` altına ekle
3. Service interface'i ve implementasyonunu yaz
4. Kullanıcıya dönecek string'ler için `IStringLocalizer<ErrorMessages>` kullan, hardcoded Türkçe string YASAK
5. Unit test yaz
6. Saydın meta repo `docs/architecture/api-contract.md`'ı güncelle (bu repo'da değil — bkz. Dokümantasyon Standardı tablosu)

---

## Yasak Listesi

- **Dapper** — YASAK (EF Core kullan)
- **Raw `Npgsql.NpgsqlConnection`** doğrudan açmak — YASAK (DbContext kullan). Yalnız yukarıda
  tanımlı `InstallationRepository` paylaşılan `NpgsqlDataSource` istisnası geçerlidir.
- Controller sınıfı (`[ApiController]`, `ControllerBase`) — YASAK
- `new HttpClient()` — YASAK. Yalnız yukarıda belgelenen calendar-data one-shot composition-root
  istisnası geçerlidir; servis/adapter koduna genişletilemez.
- `double`, `float` finansal değer için — YASAK
- SQL string interpolation — YASAK
- Kafka, Dapr, gRPC (ADR olmadan) — YASAK
- Exception'ı sessizce yutmak — YASAK
- `Thread.Sleep()` — YASAK (kullan: `await Task.Delay()`)
- `DateTime.Now` finansal tarihler için — YASAK (kullan: `DateTimeOffset.UtcNow` veya `DateOnly`)
- API key'i appsettings.json'a yazmak — YASAK
- Log mesajında string interpolation — YASAK (kullan: parametreli mesaj)
- `Console.WriteLine` veya `Debug.WriteLine` — YASAK (kullan: `ILogger<T>`). Yalnız calendar-data
  one-shot CLI'nin bounded machine-readable process contract'ı belgeli istisnadır.
- Exception handler olmadan endpoint yazmak — YASAK (GlobalExceptionHandler her zaman var)
- Kullanıcıya dönecek string'lerde hardcoded Türkçe/İngilizce — YASAK (kullan: `IStringLocalizer<ErrorMessages>`)

---

## Dokümantasyon Standardı

### Nereye Yazılır?

| Kapsam | Konum |
|--------|-------|
| Backend dokümantasyon haritası (başlangıç) | `docs/README.md` |
| Servis mimarisi (katmanlar, sınırlar, resilience, cache, DB erişim) | `docs/architecture.md` |
| Backend derin teknik referanslar (DB şeması, observability, activity-logging) | Bu repo `docs/architecture/` (backend-özgü) |
| .NET geliştirme iş akışı (komutlar, Docker, migration, test, sorun giderme) | `docs/development-guide.md` |
| Backend ölçeklendirme/ops kontrol listesi | `docs/high-traffic-checklist.md` |
| İstemci↔backend API sözleşmesi (`api-contract.md`) + proje geneli mimari (istemci+servis, overview) | Saydın meta repo `docs/` dizini |
| Backend/servis-özgü mimari kararlar (ADR) | Bu repo `docs/decisions/` (bağımsız `ADR-001+` uzayı) |
| Ürün / çapraz-bileşen mimari kararlar (ADR) | Saydın meta repo `docs/decisions/` (bağımsız `ADR-001..ADR-014` uzayı) |

### Kurallar

- **Diyagram ve akış şemaları Mermaid ile çizilir** — ASCII art YASAK. Markdown dosyalarında ` ```mermaid ` blokları kullan.
- **Backend'e özgü** her doküman `docs/` içine gider — Saydın meta repo'sundaki kök `docs/` içine konmaz.
- Kök `docs/`'a yalnızca birden fazla bileşeni (istemci + servisler) kapsayan belgeler eklenir.
- Yeni endpoint, adapter veya servis eklendiğinde ilgili `docs/` dosyaları güncellenir.
- Yeni API endpoint eklendiğinde Saydın meta repo `docs/architecture/api-contract.md` de güncellenir.
- **ADR yer seçimi (F4-10):** karar yalnızca backend'i mi yoksa istemci/ürünü de mi
  ilgilendiriyor? Yalnız backend/altyapı → bu repo `docs/decisions/ADR-XXX-<konu>.md`
  (bağımsız numaralandırma); istemci+servis veya ürün/UX/legal kapsıyorsa Saydın meta repo
  `docs/decisions/`. İki ADR uzayı kasıtlı olarak ayrıdır; numara çakışması beklenir ve
  sorun değildir. Detay: [`docs/decisions/README.md`](docs/decisions/README.md).
- Dokümanlar kod değişikliğiyle aynı commit'te güncellenir; ayrı PR açılmaz.
