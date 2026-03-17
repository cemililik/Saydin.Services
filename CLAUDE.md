# Saydın Services — Agent Kuralları

## Proje Bağlamı

Saydın, Türk kullanıcılara yönelik finansal "ya alsaydım?" hesaplama uygulamasının backend'idir.

Bu repo iki .NET 10 servisini ve ortak kütüphaneyi içerir:
- `Saydin.Api` — Flutter uygulamasına HTTP endpoint'leri sunan Minimal API servisi
- `Saydin.PriceIngestion` — Dış finansal API'lerden fiyat verisi çeken background worker
- `Saydin.Shared` — Her iki servisin kullandığı ortak entity, exception ve extension sınıfları

---

## Geliştirme Ortamı Kuralı (KRİTİK)

**Lokal makinede .NET 10 SDK kurulu değildir.**
`dotnet build`, `dotnet test`, `dotnet run` gibi komutları **doğrudan çalıştırma.**
Tüm build, test ve çalıştırma işlemleri **Docker Compose** üzerinden yapılır:

```bash
# Servisleri başlat
docker compose up -d

# Build & test
docker compose run --rm api dotnet test

# Sadece build
docker compose run --rm api dotnet build
```

Lokal `dotnet` bulunamadı diye debelenme — her zaman Docker Compose kullan.

---

## Commit Kuralı (KRİTİK)

**Kod değişikliklerini commit etmeden önce mutlaka build ve testleri çalıştır.**

```bash
docker compose run --rm api dotnet build
docker compose run --rm api dotnet test
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
- **Migration:** `dotnet ef migrations add <Ad> --project src/Saydin.Shared --startup-project src/Saydin.Api`
- **HTTP Client:** `IHttpClientFactory` ile kayıtlı named client'lar — `new HttpClient()` YASAKTIR

### Servis Sınırları

- `Saydin.Api` hiçbir dış finansal API'ye (TCMB, CoinGecko, GoldAPI, Twelve Data) HTTP isteği ATMAZ
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
- Kullanıcıya dönecek hata mesajları Türkçe olur
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
PriceNotFoundExceptionHandler → ValidationExceptionHandler → ExternalApiExceptionHandler → GlobalExceptionHandler
```

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
// Program.cs
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql", tags: ["db"])
    .AddRedis(redisConnectionString, name: "redis", tags: ["cache"]);

app.MapHealthChecks("/health");
```

---

## Dış API Adaptörleri

Her adaptör şu kuralları izler:

```csharp
public interface IExternalApiAdapter
{
    Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        string assetSymbol,
        DateOnly from,
        DateOnly to,
        CancellationToken ct);
}
```

- Polly ile **retry** (3 deneme, exponential backoff)
- Polly ile **circuit breaker** (5 ardışık hata → devre açılır)
- Her istekte 30 saniye timeout
- 429 (rate limit) alındığında exponential backoff uygulanır

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
# Yeni migration oluştur
dotnet ef migrations add <MigrationAdı> \
  --project src/Saydin.Shared \
  --startup-project src/Saydin.Api

# Veritabanını güncelle
dotnet ef database update \
  --project src/Saydin.Shared \
  --startup-project src/Saydin.Api
```

---

## Test Kuralları

- `Services/` katmanındaki her public method için unit test zorunludur
- Test adlandırma: `MethodName_Scenario_ExpectedResult`
- Dış adaptörler için en az deserializasyon testi (contract test) gerekir
- Veritabanı testleri için real PostgreSQL (Docker) kullanılır — mock yasak

---

## Yeni Özellik Ekleme

### Yeni Asset Eklemek
`.claude/commands/add-asset.md` dosyasındaki 8 adımlı checklist'i uygula.

### Yeni Endpoint Eklemek
1. `Endpoints/` klasöründe ilgili extension method'a ekle
2. Request/Response record type'larını `Models/` altına ekle
3. Service interface'i ve implementasyonunu yaz
4. Unit test yaz
5. `docs/architecture/api-contract.md`'ı güncelle

---

## Yasak Listesi

- **Dapper** — YASAK (EF Core kullan)
- **Raw `Npgsql.NpgsqlConnection`** doğrudan açmak — YASAK (DbContext kullan)
- Controller sınıfı (`[ApiController]`, `ControllerBase`) — YASAK
- `new HttpClient()` — YASAK
- `double`, `float` finansal değer için — YASAK
- SQL string interpolation — YASAK
- Kafka, Dapr, gRPC (ADR olmadan) — YASAK
- Exception'ı sessizce yutmak — YASAK
- `Thread.Sleep()` — YASAK (kullan: `await Task.Delay()`)
- `DateTime.Now` finansal tarihler için — YASAK (kullan: `DateTimeOffset.UtcNow` veya `DateOnly`)
- API key'i appsettings.json'a yazmak — YASAK
- Log mesajında string interpolation — YASAK (kullan: parametreli mesaj)
- `Console.WriteLine` veya `Debug.WriteLine` — YASAK (kullan: `ILogger<T>`)
- Exception handler olmadan endpoint yazmak — YASAK (GlobalExceptionHandler her zaman var)

---

## Dokümantasyon Standardı

### Nereye Yazılır?

| Kapsam | Konum |
|--------|-------|
| Servis mimarisi (katmanlar, sınırlar, resilience, cache, DB erişim) | `src/Saydin.Services/docs/architecture.md` |
| .NET geliştirme iş akışı (komutlar, Docker, migration, test, sorun giderme) | `src/Saydin.Services/docs/development-guide.md` |
| Proje geneli mimari (istemci + servisler arası ilişki, API sözleşmesi, DB şeması) | Kök `docs/` dizini |
| Mimari kararlar (ADR) | Kök `docs/decisions/` dizini |

### Kurallar

- **Backend'e özgü** her doküman `src/Saydin.Services/docs/` içine gider — kök `docs/` içine konmaz.
- Kök `docs/`'a yalnızca birden fazla bileşeni (istemci + servisler) kapsayan belgeler eklenir.
- Yeni endpoint, adapter veya servis eklendiğinde ilgili `docs/` dosyaları güncellenir.
- Yeni API endpoint eklendiğinde kök `docs/architecture/api-contract.md` de güncellenir.
- Büyük mimari karar alındığında kök `docs/decisions/ADR-XXX-<konu>.md` oluşturulur.
- Dokümanlar kod değişikliğiyle aynı commit'te güncellenir; ayrı PR açılmaz.
