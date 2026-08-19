# Saydın — Observability Mimarisi

> **Not:** Observability kurallarının (log seviyeleri, structured logging zorunluluğu,
> exception handler deseni, custom span/metrik kuralları, health check) **normatif kaynağı**
> kök `CLAUDE.md` → "Observability Kuralları" bölümüdür. Bu doküman o kuralların **detaylı
> referansıdır**: kod ve konfigürasyonun fiili durumunu (Serilog, OpenTelemetry tracing/metrics,
> Prometheus, health check ve exception zinciri) örnekleriyle açıklar.

## Genel Yaklaşım

Üç observability sütunu ("three pillars") birlikte uygulanır:

| Sütun | Araç | Nerede Görülür |
|-------|------|----------------|
| **Structured Logging** | Serilog + Console (JSON) + OTLP | Collector'ın disk kuyruğu → Loki |
| **Distributed Tracing** | OpenTelemetry | Collector'ın disk kuyruğu → Tempo |
| **Metrics** | OpenTelemetry + Prometheus | Prometheus; pipeline/queue metrikleri dahil |

Üretimde trace ve loglar **OTLP** üzerinden private management network'ündeki Collector'a
gönderilir. Collector her iki export yolunda da bounded retry ve disk-backed queue kullanır;
trace'leri Tempo'ya, logları Loki'ye yazar. Tempo/Loki için kalıcı external volume ve bounded
retention zorunludur, hiçbirinin host portu yayınlanmaz. Collector kuyruk/export arızaları
Prometheus tarafından izlenir. Geliştirmede `devtools` profiliyle Aspire Dashboard opsiyonel
bir görüntüleme yüzeyi olarak kullanılabilir; üretim forensic backend'i değildir.

Metrikler ayrıca private management listener'daki `GET :9090/metrics` üzerinden Prometheus
tarafından doğrudan kazınır (Prometheus exporter, OTLP'den bağımsız ikinci yol).

---

## Geliştirme Ortamı Araçları

Aşağıdaki opsiyonel araçlar `docker compose --profile devtools up -d` ile ayağa kalkar ve
yalnızca loopback'e (`127.0.0.1`) bağlanır:

| Araç | URL | Amaç |
|------|-----|-------|
| **Aspire Dashboard** | http://localhost:18888 | Log, trace ve metrik tek arayüzde |
| **Prometheus** | http://localhost:9090 | Metrik sorgulama (PromQL) |
| **pgAdmin** | http://localhost:5050 | PostgreSQL yönetimi |
| **Redis Insight** | http://localhost:5540 | Redis izleme ve yönetimi |

> Geliştirme OTLP gRPC ucu: `localhost:4317` (Aspire Dashboard container içinde `18889`).
> Prometheus ek olarak `postgres-exporter` ve `redis_exporter` hedeflerini de kazır.

---

## Logging

### Yaklaşım: Serilog + Console (JSON) + OTLP

Serilog kullanılır çünkü:
- `.ForContext<T>()` ile zengin log enrichment
- `{@object}` ile structured (JSON) nesne loglama
- OTLP sink aracılığıyla geliştirmede Aspire'a, üretimde Collector → Loki'ye gönderim
- Trace ID ve Span ID otomatik olarak log'a eklenir (trace-log korelasyonu)

### Konfigürasyon

`Program.cs` önce bir **bootstrap logger** kurar (Console), ardından host kurulurken asıl
logger'ı `appsettings.json` (`ReadFrom.Configuration`) ve DI'dan (`ReadFrom.Services`) okuyarak
yapılandırır:

```csharp
// ─── Bootstrap Logger ─── (build başlamadan önceki erken hatalar için)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// ─── Asıl Serilog ───
var serviceVersion = ApiServiceVersionContract.Parse(builder.Configuration, builder.Environment);
builder.Host.UseSerilog((ctx, services, cfg) =>
{
    var otlpEndpoint = ctx.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317";

    cfg
        .ReadFrom.Configuration(ctx.Configuration)   // MinimumLevel + Override appsettings'ten
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())   // Yapısal JSON
        .WriteTo.OpenTelemetry(opts =>                                  // OTLP → configured collector
        {
            opts.Endpoint = otlpEndpoint;
            opts.Protocol = OtlpProtocol.Grpc;
            opts.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = "saydin-api",
                ["service.version"] = serviceVersion
            };
        });
});
```

Minimum seviye ve override'lar `appsettings.json` → `Serilog` bölümünden gelir:

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft.AspNetCore": "Warning",
      "System.Net.Http": "Warning"
    }
  }
}
```

HTTP istek logları `app.UseSerilogRequestLogging()` ile tek satırlık structured kayıt olarak
üretilir (exception handler'dan sonra pipeline'a girer).

### Log Seviyeleri

| Seviye | Ne Zaman |
|--------|----------|
| `Error` | Beklenmeyen exception, dış API tamamen başarısız |
| `Warning` | Beklenen ama anormal durum (fiyat bulunamadı, rate limit, limit aşımı) |
| `Information` | İş akışı adımları (ingestion başladı/bitti, hesaplama yapıldı, tier-feature kapalı) |
| `Debug` | Yalnızca Development ortamında, detay bilgi |

### Log Kuralları

```csharp
// DOĞRU ✓ — structured logging, parametreli mesaj
_logger.LogInformation("Fiyat hesaplandı: {Symbol} {BuyDate} → {ProfitPercent}%",
    symbol, buyDate, profitPercent);

// YANLIŞ ✗ — string interpolation ile log (structured değil, query yapılamaz)
_logger.LogInformation($"Fiyat hesaplandı: {symbol} {buyDate} → {profitPercent}%");
```

`Console.WriteLine` / `Debug.WriteLine` yasaktır — her zaman `ILogger<T>` veya Serilog kullanılır
(bkz. CLAUDE.md "Yasak Listesi").

---

## Distributed Tracing

### Konfigürasyon

```csharp
var otlpEndpointUri = new Uri(
    builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317");
var serviceVersion = ApiServiceVersionContract.Parse(builder.Configuration, builder.Environment);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService("saydin-api", serviceVersion: serviceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName.ToLowerInvariant()
        }))
    .WithTracing(tracing => tracing
        .AddSource(SaydinActivitySource.Instance.Name)   // custom iş akışı span'ları
        .AddAspNetCoreInstrumentation(opts =>
        {
            opts.RecordException = true;
            // Health ve metrics trace'leri hariç tutulur (gürültü)
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                                 && !ctx.Request.Path.StartsWithSegments("/metrics");
        })
        .AddHttpClientInstrumentation(opts => opts.RecordException = true)
        .AddOtlpExporter(opts =>
        {
            opts.Endpoint = otlpEndpointUri;
            opts.Protocol = OtlpExportProtocol.Grpc;
        }));
```

Dış API adapter'ları `AddHttpClientInstrumentation()` ile otomatik izlenir. Public liveness
(`/health/live`) ile management-only readiness (`/health/ready`) ve Prometheus scrape
(`/metrics`) yolları trace'e dahil edilmez.

### Merkezi ActivitySource

ActivitySource `Saydin.Shared`'de merkezi tanımlanır; **kaynak adı `"Saydin"`** olduğu için
hem `Saydin.Api` hem `Saydin.PriceIngestion` aynı source'u paylaşır (servis ayrımı
`service.name` resource attribute'u ile yapılır):

```csharp
// Saydin.Shared/Diagnostics/SaydinActivitySource.cs
public static class SaydinActivitySource
{
    public static readonly ActivitySource Instance = new("Saydin", "1.0.0");
}
```

### Custom Span'lar

İş akışı adımları için manuel span ekle:

```csharp
using var activity = SaydinActivitySource.Instance.StartActivity("WhatIfCalculation");
activity?.SetTag("asset.symbol", request.AssetSymbol);
activity?.SetTag("buy.date", request.BuyDate.ToString());
// ... hesaplama
activity?.SetTag("profit.percent", result.ProfitPercent);
```

---

## Metrics

### Konfigürasyon

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(SaydinMetrics.MeterName)   // iş metrikleri (meter adı: "Saydin")
        .AddAspNetCoreInstrumentation()      // HTTP request metrikleri
        .AddHttpClientInstrumentation()      // outbound HTTP metrikleri
        .AddRuntimeInstrumentation()         // GC, thread pool vb.
        .AddOtlpExporter(opts =>
        {
            opts.Endpoint = otlpEndpointUri;
            opts.Protocol = OtlpExportProtocol.Grpc;
        })
        .AddPrometheusExporter());           // management /metrics endpoint'i

// Route kaydı; port-boundary metadata'sı bunu yalnız :9090'da kabul eder.
app.MapPrometheusScrapingEndpoint("/metrics");
```

### Merkezi Meter ve Custom Metrikler

İş metrikleri `Saydin.Shared/Diagnostics/SaydinMetrics.cs`'de merkezi tanımlanır. **Meter adı
`"Saydin"`** olarak tutulur (`"Saydin.Api"` DEĞİL) — Shared kütüphane her iki servis tarafından
referans alındığı için tek isim üzerinden yayın yapılır ve servis ayrımı `service.name` resource
attribute'u ile yapılır (review F1.5-1). `Program.cs`'de bu isim `AddMeter(SaydinMetrics.MeterName)`
ile kaydedilir.

```csharp
// Saydin.Shared/Diagnostics/SaydinMetrics.cs
public static class SaydinMetrics
{
    public const string MeterName = "Saydin";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    // ... counter / histogram tanımları (aşağıdaki tablo)
}
```

Tanımlı iş metrikleri:

| Metrik | Tip | Birim | Açıklama | Tag'ler |
|--------|-----|-------|----------|---------|
| `saydin.whatif.calculations.total` | Counter&lt;long&gt; | — | Toplam ya-alsaydım hesaplama sayısı | `asset.symbol`, `user.tier` |
| `saydin.whatif.calculation.duration.ms` | Histogram&lt;double&gt; | ms | Ya-alsaydım hesaplama süresi | — |
| `saydin.price.not_found.total` | Counter&lt;long&gt; | — | Fiyat bulunamayan sorgu sayısı | — |
| `saydin.inflation.ingestion.failures.total` | Counter&lt;long&gt; | — | EVDS/TÜFE ingestion başarısızlıkları | `source`, `outcome` (`auth\|http\|other`) |
| `saydin.activity_log.write.failures.total` | Counter&lt;long&gt; | — | Activity log batch yazımı başarısızlıkları | `outcome` (`retry_exhausted\|cancelled`) |
| `saydin.activity_log.queue.drops.total` | Counter&lt;long&gt; | — | `itemDropped` callback'inin gözlediği gerçek capacity drop | `action` (allowlist) |
| `saydin.activity_log.queue.rejected_writes.total` | Counter&lt;long&gt; | — | Completed channel writer tarafından reddedilen write; drop değildir | `action` (allowlist), `reason=writer_completed` |
| `saydin.activity_log.data.truncations.total` | Counter&lt;long&gt; | — | Byte limit aşıldığı için truncate edilen `data` payload sayısı | `action` |

> **Operasyon notu:** `EvdsInflationWorker` adapter başarısızlığını şu an `ingestion_jobs`
> tablosuna her durumda yazmayabilir; alarm bu nedenle `saydin.inflation.ingestion.failures.total`
> sayacına dayanır. Activity log yazma yolundaki **sessiz veri kaybı**
> (`activity_log.write.failures` / `queue.drops` / `queue.rejected_writes` /
> `data.truncations`) yalnızca bu sayaçlardan
> izlenebilir — bu sayaçlar olmadan drop edilen kayıtlar görünmez olurdu.

Kullanım:

```csharp
SaydinMetrics.WhatIfCalculations.Add(1,
    new KeyValuePair<string, object?>("asset.symbol", symbol),
    new KeyValuePair<string, object?>("user.tier", userTier));
```

Özel metrik eklenirken `Meter` ve `Counter`/`Histogram` kullanılır; ham sayı tutulmaz
(bkz. CLAUDE.md "Metrics").

---

## Exception Handling

### Yaklaşım: 11 Handler'lı IExceptionHandler Zinciri

.NET 10'un `IExceptionHandler` interface'i ile her domain exception türü için **ayrı** handler
yazılır. `Program.cs`'deki kayıt sırası anlamlıdır: spesifik handler'lar önce gelir,
`GlobalExceptionHandler` her zaman zincirin **sonunda** (catch-all) durur. İlk `true` dönen
handler zinciri keser.

```mermaid
flowchart LR
    R["İstek → Endpoint → Service<br/>Exception fırlatıldı"] --> B["RequestBodyTooLargeExceptionHandler<br/>413"]
    B --> V["ValidationExceptionHandler<br/>400"]
    V --> F["FeatureDisabledExceptionHandler<br/>403"]
    F --> P["PriceNotFoundExceptionHandler<br/>404"]
    P --> A["AssetNotFoundExceptionHandler<br/>404"]
    A --> SN["ScenarioNotFoundExceptionHandler<br/>404"]
    SN --> SL["ScenarioLimitExceededExceptionHandler<br/>422"]
    SL --> D["DailyLimitExceededExceptionHandler<br/>429"]
    D --> Q["QuotaUnavailableExceptionHandler<br/>503"]
    Q --> E["ExternalApiExceptionHandler<br/>502"]
    E --> G["GlobalExceptionHandler<br/>500 + traceId"]
```

HTTP kod konvansiyonu:

| Handler | Exception | Durum | `Type` (RFC 7807) | Notlar |
|---------|-----------|:-----:|-------------------|--------|
| `RequestBodyTooLargeExceptionHandler` | `RequestBodyTooLargeException` | 413 | `…/errors/payload-too-large` | Endpoint byte limiti ve stable code |
| `ValidationExceptionHandler` | `ValidationException` | 400 | `…/errors/validation` | Yalnız domain `ValidationException`; jenerik `ArgumentException` Global'a düşer (P1R-003). `field` extension'ı opsiyonel |
| `FeatureDisabledExceptionHandler` | `FeatureDisabledException` | 403 | `…/errors/feature-disabled` | Tier ile kapatılan özellik; `feature` extension'ı. 404 değil → istemci upsell gösterebilsin (F4-14) |
| `PriceNotFoundExceptionHandler` | `PriceNotFoundException` | 404 | `…/errors/price-not-found` | `nearestDates` extension'ı |
| `AssetNotFoundExceptionHandler` | `AssetNotFoundException` | 404 | `…/errors/asset-not-found` | — |
| `ScenarioNotFoundExceptionHandler` | `ScenarioNotFoundException` | 404 | `…/errors/scenario-not-found` | — |
| `ScenarioLimitExceededExceptionHandler` | `ScenarioLimitExceededException` | 422 | `…/errors/scenario-limit-exceeded` | Geçerli ama domain kotasını ihlal eden istek; `limit` extension'ı |
| `DailyLimitExceededExceptionHandler` | `DailyLimitExceededException` | 429 | `…/errors/daily-limit-exceeded` | `limit` + `resetAt` (UTC `DateTimeOffset`, `TimeProvider`'dan) extension'ları |
| `QuotaUnavailableExceptionHandler` | `QuotaUnavailableException` | 503 | `…/errors/quota-unavailable` | Finite quota Redis arızasında fail-closed stable code |
| `ExternalApiExceptionHandler` | `ExternalApiException` | 502 | `…/errors/external-api` | `source` extension'ı; `Warning` loglar |
| `GlobalExceptionHandler` | (catch-all) | 500 | `…/errors/internal-error` | `Error` loglar, her zaman `traceId` döner |

> Dağıtık security limiter middleware'i bu zincirden bağımsızdır. Limit aşımında 429,
> Redis/client-IP güven sınırı arızasında 503 döndürür; her iki yanıt stable error code ve
> `traceId` taşır (bkz. "Dağıtık Abuse Limiti ve Günlük Kota").

### Ortak Kurallar

- Resource-backed handler'lar `IStringLocalizer<ErrorMessages>` ile kullanıcı metnini
  lokalize eder. Her yanıtın makinece okunabilen stable `code` alanı dil bağımsızdır.
- Tüm handler'lar **RFC 7807** `ProblemDetails` döner ve `Extensions["traceId"]` ekler.
  `Activity.Current` null olabileceği için `traceId = Activity.Current?.TraceId.ToString()
  ?? context.TraceIdentifier` deseni kullanılır.
- Beklenen-anormal durumlar `LogWarning`, beklenmeyen exception'lar `LogError` ile loglanır.
  Exception'ı sessizce yutan catch block YASAK.

### Implementasyon Deseni

```csharp
// Saydin.Api/Exceptions/PriceNotFoundExceptionHandler.cs
public sealed class PriceNotFoundExceptionHandler(
    ILogger<PriceNotFoundExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not PriceNotFoundException ex)
            return false;   // bu handler işleyemez → zincir bir sonrakine geçer

        logger.LogWarning(
            "Fiyat bulunamadı: {Symbol} / {Date}",
            ex.AssetSymbol, ex.Date);

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/price-not-found",
            Title = localizer["PriceNotFound"],                     // lokalize
            Status = StatusCodes.Status404NotFound,
            Detail = string.Format(localizer["PriceNotFoundDetail"], // lokalize, parametreli
                ex.Date.ToString("yyyy-MM-dd"), ex.AssetSymbol),
            Extensions =
            {
                ["traceId"] = Activity.Current?.TraceId.ToString(),
                ["nearestDates"] = ex.NearestAvailableDates
            }
        }, ct);

        return true;   // işlendi → zincir kesilir
    }
}

// Saydin.Api/Exceptions/GlobalExceptionHandler.cs — zincirin sonu (catch-all)
public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        logger.LogError(exception,
            "İşlenmemiş exception: {ExceptionType} — TraceId: {TraceId}",
            exception.GetType().Name, traceId);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/internal-error",
            Title = localizer["ServerError"],
            Status = StatusCodes.Status500InternalServerError,
            Detail = localizer["UnexpectedError"],
            Extensions = { ["traceId"] = traceId }
        }, ct);

        return true;
    }
}
```

### Kayıt (Program.cs) — Sıra Önemli

```csharp
builder.Services.AddProblemDetails();
// Spesifik handler'lar önce, GlobalExceptionHandler en sonda:
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<FeatureDisabledExceptionHandler>();
builder.Services.AddExceptionHandler<PriceNotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<AssetNotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ScenarioNotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ScenarioLimitExceededExceptionHandler>();
builder.Services.AddExceptionHandler<DailyLimitExceededExceptionHandler>();
builder.Services.AddExceptionHandler<ExternalApiExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

app.UseExceptionHandler();
```

### TraceId İstemciye Döner

Her yanıtta `traceId` extension'ı bulunur (özellikle 5xx'te kritik). İstemci bu ID'yi
destek talebine ekler; operatör üretimde Tempo'da, geliştirmede opsiyonel Aspire arayüzünde
tam çağrı zincirini (`endpoint → service → repository → DB query`) bulur.

---

## Dağıtık Abuse Limiti ve Günlük Kota

İstek sınırları process-local değildir. Redis TIME tabanlı atomik script, HMAC ile
pseudonymize edilmiş istemci IP'si, IPv4 `/24` veya IPv6 `/64` ağı ve kimliği doğrulanmış
installation principal için ayrı bucket'lar uygular. Principal bucket'ı credential
çözüldükten sonra alınır. Finite günlük ürün kotası ayrıca nonce'lu `QuotaLease` üretir;
release exact lease key'ini kullanır ve tekrar/gün dönümü karşısında idempotenttir.

Üretim deployment validator'ı limiter kapalıysa, HMAC secret/file yoksa veya trusted-proxy
sözleşmesi wildcard/placeholder ise başlamayı reddeder. Redis arızası, bilinmeyen istemci IP'si
ve malformed/untrusted forwarded IP **503**; gerçek limit aşımı **429** üretir. Sınırsız ürün
planı no-op lease kullanabilir, finite kota hiçbir zaman fail-open değildir.

Observability açısından:
- Public `/health/live` yalnız process liveness'tır; dependency ayrıntısı taşımaz.
- `/health/ready` ve `/metrics` yalnız private `:9090` management listener'ındadır ve dış
  istemci limiter yüzeyine açılmaz.
- Reddetme yanıtı 429 + RFC 7807 `ProblemDetails` (`type=…/errors/rate-limited`), `IStringLocalizer`
  ile lokalize `title`/`detail`, `traceId` extension'ı ve `Retry-After` header taşır — exception
  zinciriyle tutarlı şekil.
- IP/network limiti trusted-proxy çözümlemesinden sonra, principal limiti credential doğrulaması
  sırasında çalışır. İki API replikası aynı Redis bucket'larını paylaşır.

---

## Health Checks

### Konfigürasyon

```csharp
builder.Services
    .AddHealthChecks()
    // PostgreSQL: paylaşılan NpgsqlDataSource üzerinden "SELECT 1" — ayrı bağlantı string'i değil.
    .AddAsyncCheck("postgresql", async ct =>
    {
        await using var conn = await npgsqlDataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return HealthCheckResult.Healthy();
    }, tags: ["db"])
    .AddRedis(
        builder.Configuration.GetConnectionString("Redis")!,
        name: "redis",
        tags: ["cache"]);

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
```

- PostgreSQL kontrolü hazır `AddNpgSql(...)` yerine, uygulamanın gerçekte kullandığı singleton
  `NpgsqlDataSource` (aynı connection pool) üzerinden `SELECT 1` çalıştıran bir `AddAsyncCheck`
  ile yapılır — gerçek pool sağlığını yansıtır.
- Redis zorunlu readiness dependency değildir; cache ve distributed security yüzeyleri kendi
  fail-closed/degraded sözleşmelerini uygular.
- `/health/live` public `:8080` listener'ında yalnız process kontrolüdür. PostgreSQL readiness
  `/health/ready` ve scrape `/metrics` private `:9090` listener'ında kalır. Port-boundary
  middleware yanlış listener/yol birleşimini 404 ile reddeder.

---

## Activity Logging (Channel Pattern)

Kullanıcı/istek aktiviteleri `activity_logs` (TimescaleDB hypertable) tablosuna **asenkron** yazılır.
İstek yolunda DB yazımını bloklamamak için bounded `Channel<ActivityLog>` (kapasite 10.000,
`FullMode = DropWrite`, `SingleReader`) kullanılır:

- `ActivityLogMiddleware` (transient `IMiddleware`) exception handler'dan **sonra**, endpoint
  mapping'den **önce** çalışır; pipeline tamamlanınca nihai `Response.StatusCode`'u kayda yazar →
  4xx/5xx istekler de doğru loglanır (review C-3).
- `ChannelActivityLogger` kayıtları kuyruğa yazar. `DropWrite` doluyken `TryWrite=true`
  döndüğünden gerçek kayıp callback'te `saydin.activity_log.queue.drops.total` olarak ölçülür.
  `TryWrite=false` completed writer'dır ve ayrı
  `saydin.activity_log.queue.rejected_writes.total` sayacına gider; drop sayılmaz.
- Drop/rejected warning'leri ayrı birer dakikalık pencereyle rate-limit edilir; action
  metric ve loglarda allowlist dışıysa `unknown` olarak normalize edilir.
- `ActivityLogWriter` (hosted service) kuyruğu okuyup batch UPSERT yapar; kalıcı başarısızlıkta
  `saydin.activity_log.write.failures.total` (`outcome=retry_exhausted|cancelled`), byte limit
  aşımında `saydin.activity_log.data.truncations.total` sayaçları artar.

Bu dört sayaç, sessizce düşen/reddedilen/kısaltılan kayıtlar için **tek görünürlük kaynağıdır**.

---

## Pratik Kullanım Senaryoları

### Senaryo 1: Yavaş Hesaplama Tespiti

1. Prometheus'ta `saydin_whatif_calculation_duration_ms` histogram'ını izle.
2. P99 > 500ms uyarısı: hangi `asset.symbol`?
3. Tempo'da `WhatIfCalculation` span'ını bul → hangi alt-adım yavaş?

### Senaryo 2: Dış API / Downstream Hata Takibi

1. `ExternalApiException` → 502 logları Loki'de `Warning` olarak görünür.
2. Her log'da `traceId` var → ilgili `AddHttpClientInstrumentation` span'ını bul.
3. Prometheus'ta `http.client.request.duration` ile trend analizi.

### Senaryo 3: Üretim Hata Analizi (5xx)

1. Kullanıcı hata bildiriyor, yanıttaki `traceId: abc123` değerini paylaşıyor.
2. Tempo'da `abc123` trace kimliğini ara.
3. Tam çağrı zinciri: endpoint → service → repository → DB query.

### Senaryo 4: Sessiz Log Kaybı / Limit Alarmı

1. `saydin.activity_log.queue.drops.total`, `…queue.rejected_writes.total` veya
   `…write.failures.total` artışı → activity log
   yazma yolunda darboğaz/hata.
2. `saydin.inflation.ingestion.failures.total` (`outcome` tag'i) → EVDS TÜFE ingestion sorunu.
3. `saydin.price.not_found.total` artışı → eksik fiyat verisi / yeni asset backfill gecikmesi.

Collector export failure veya disk kuyruğu kapasite alarmında
[`../runbooks/telemetry-pipeline.md`](../runbooks/telemetry-pipeline.md) izlenir. Queue verisi
external volume'da tutulur; Tempo/Loki retention varsayılanı bounded'dır. Bu yerel private
backend off-site arşiv değildir; olay delili saklama süresi ve legal hold operatör politikasıdır.

---

## İlgili Belgeler

- Kök `CLAUDE.md` → "Observability Kuralları" (normatif kurallar).
- [`../architecture.md`](../architecture.md) → exception zinciri, dağıtık limiter + kota,
  feature flag → 403, lokalizasyon middleware zinciri.
- [`../cache-strategy.md`](../cache-strategy.md) → Redis cache key/TTL ve fail-open prensibi.
- ADR'ler [`../decisions/`](../decisions/) altındadır. Özellikle ADR-003 dağıtık limiter/kota,
  ADR-006 activity minimizasyonu ve ADR-010 installation principal/retention sözleşmesini
  tanımlar.
