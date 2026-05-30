using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using Scalar.AspNetCore;
using Saydin.Api.BackgroundServices;
using Saydin.Api.Endpoints;
using Saydin.Api.Exceptions;
using Saydin.Api.Middleware;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Data;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;
using StackExchange.Redis;

// ─── Bootstrap Logger ────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog ─────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) =>
    {
        var otlpEndpoint = ctx.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317";

        cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
            .WriteTo.OpenTelemetry(opts =>
            {
                opts.Endpoint = otlpEndpoint;
                opts.Protocol = OtlpProtocol.Grpc;
                opts.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = "saydin-api",
                    ["service.version"] = "1.0.0"
                };
            });
    });

    // ─── OpenTelemetry ───────────────────────────────────────────────────────
    var otlpEndpointUri = new Uri(
        builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService("saydin-api", serviceVersion: "1.0.0")
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = builder.Environment.EnvironmentName.ToLowerInvariant()
            }))
        .WithTracing(tracing => tracing
            .AddSource(SaydinActivitySource.Instance.Name)
            .AddAspNetCoreInstrumentation(opts =>
            {
                opts.RecordException = true;
                opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                                     && !ctx.Request.Path.StartsWithSegments("/metrics");
            })
            .AddHttpClientInstrumentation(opts => opts.RecordException = true)
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = otlpEndpointUri;
                opts.Protocol = OtlpExportProtocol.Grpc;
            }))
        .WithMetrics(metrics => metrics
            .AddMeter(SaydinMetrics.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = otlpEndpointUri;
                opts.Protocol = OtlpExportProtocol.Grpc;
            })
            .AddPrometheusExporter());

    // ─── Localization ──────────────────────────────────────────────────────────
    // ResourcesPath BİLEREK ayarlanmaz: resx dosyaları Resources/ErrorMessages.cs (namespace
    // Saydin.Api) ile DependentUpon olduğundan "Saydin.Api.ErrorMessages.resources" olarak
    // gömülür — "Resources" segmenti YOKTUR. ResourcesPath="Resources" verilirse factory
    // "Saydin.Api.Resources.ErrorMessages" arar → her lookup ıskalar ve ham resx KEY'i döner
    // (tr/en ayrımı kaybolur). Bkz. ErrorMessagesLocalizationTests (regresyon kilidi).
    builder.Services.AddLocalization();

    // ─── Exception Handling ──────────────────────────────────────────────────
    builder.Services.AddProblemDetails();
    // Sıralı zincir: spesifik handler'lar önce, GlobalExceptionHandler en sonda.
    builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
    builder.Services.AddExceptionHandler<FeatureDisabledExceptionHandler>();
    builder.Services.AddExceptionHandler<PriceNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<AssetNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<ScenarioNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<ScenarioLimitExceededExceptionHandler>();
    builder.Services.AddExceptionHandler<DailyLimitExceededExceptionHandler>();
    builder.Services.AddExceptionHandler<ExternalApiExceptionHandler>();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // ─── JSON Serialization ──────────────────────────────────────────────────
    builder.Services.ConfigureHttpJsonOptions(opts =>
    {
        opts.SerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    });

    // ─── OpenAPI ─────────────────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    // ─── NpgsqlDataSource (singleton — tüm DbContext'ler aynı pool'u paylaşır) ───
    var pgConnection = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres yapılandırılmamış.");

    var npgsqlDataSource = new NpgsqlDataSourceBuilder(pgConnection)
        .MapEnum<AssetCategory>("asset_category")
        .Build();
    builder.Services.AddSingleton(npgsqlDataSource);

    // ─── EF Core ─────────────────────────────────────────────────────────────
    // F2.3-1 ([C-C-3/10/13], [G-C-01]): API read-heavy bir servistir; tracking ihtiyacı
    // hemen hiçbir endpoint'te yok. Global NoTracking sorgu maliyetini düşürür
    // (change tracker entries oluşmaz). Mutasyon gerektiren tek nokta SavedScenarioRepository
    // — orada explicit `AsTracking()` çağrılır.
    builder.Services.AddDbContext<SaydinDbContext>(options =>
        options.UseNpgsql(npgsqlDataSource, npgsql =>
            npgsql.MapEnum<AssetCategory>("asset_category"))
               .UseSnakeCaseNamingConvention()
               .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

    // ─── Health Checks ───────────────────────────────────────────────────────
    builder.Services
        .AddHealthChecks()
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

    // ─── Redis ───────────────────────────────────────────────────────────────
    // AbortOnConnectFail=false: Redis startup'ta down olsa bile API ayağa kalkar.
    // Cache-aside pattern gereği Redis yoksa DB'ye düşülür; health check Unhealthy raporlar.
    var redisConnection = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis yapılandırılmamış.");

    var redisOptions = ConfigurationOptions.Parse(redisConnection);
    redisOptions.AbortOnConnectFail = false;

    // APIR-027 ([C-A-1]): Connect (blocking) → ConnectAsync + Lazy. Startup'ta
    // ana thread bloğu kalktı; cache miss riski yine yok (AbortOnConnectFail=false
    // sayesinde Redis down olsa bile API ayağa kalkar, ilk istek cache-aside).
    // Task<ConnectionMultiplexer> kovaryant değil → açıkça IConnectionMultiplexer cast.
    var redisLazy = new Lazy<Task<IConnectionMultiplexer>>(
        async () => await ConnectionMultiplexer.ConnectAsync(redisOptions),
        LazyThreadSafetyMode.ExecutionAndPublication);
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        // GetAwaiter().GetResult() startup'ta tek seferlik; sonraki çağrılarda
        // cached Task instance'ı tamamlanmış olur → sync hot-path yok.
        return redisLazy.Value.GetAwaiter().GetResult();
    });

    // ─── Response Compression ────────────────────────────────────────────────
    builder.Services.AddResponseCompression(opts => opts.EnableForHttps = true);

    // ─── Options ─────────────────────────────────────────────────────────────
    // SVCR-008: PlanOptions startup'ta validate edilir — negatif limit/feature
    // sızıntısı fail-fast.
    builder.Services.AddOptions<PlanOptions>()
        .Bind(builder.Configuration.GetSection(PlanOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(o =>
        {
            var ctx = new System.ComponentModel.DataAnnotations.ValidationContext(o);
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            return System.ComponentModel.DataAnnotations.Validator.TryValidateObject(o, ctx, results, true);
        }, "PlanOptions geçersiz; bkz. application logs")
        .ValidateOnStart();

    // ─── Repositories & Services ─────────────────────────────────────────────
    // F3.1-5 / SVCR-007/025: TimeProvider — servisler DateTime.UtcNow yerine bunu
    // enjekte eder; testlerde FakeTimeProvider ile saat dondurulur (gün dönümü flaky'liği biter).
    builder.Services.AddSingleton(TimeProvider.System);
    // F2.2-3 ([C-B-CC-5]): scoped cihaz kimliği — RequireDeviceId filter doldurur,
    // iş service'leri (WhatIf/Dca/SavedScenario/AppConfig) `deviceId` parametresi yerine okur.
    builder.Services.AddScoped<DeviceContext>();
    builder.Services.AddScoped<IDeviceContext>(sp => sp.GetRequiredService<DeviceContext>());
    builder.Services.AddScoped<IPriceRepository, PriceRepository>();
    builder.Services.AddScoped<IAssetService, AssetService>();
    builder.Services.AddScoped<IInflationRepository, InflationRepository>();
    builder.Services.AddScoped<IDailyLimitGuard, DailyLimitGuard>();
    builder.Services.AddScoped<IWhatIfCalculator, WhatIfCalculator>();
    builder.Services.AddScoped<IDcaCalculator, DcaCalculator>();
    builder.Services.AddScoped<ISavedScenarioRepository, SavedScenarioRepository>();
    builder.Services.AddScoped<ISavedScenarioService, SavedScenarioService>();
    builder.Services.AddScoped<IAppConfigService, AppConfigService>();
    builder.Services.AddSingleton<IRedisCacheHelper, RedisCacheHelper>();
    builder.Services.AddScoped<IAssetNameLocalizer, AssetNameLocalizer>();
    // F2.2-12: last_seen_at throttle penceresi process-local (in-memory map) tutulur;
    // sticky session olmadan deploy edilse bile pencere ihlali kullanıcı için
    // semantik kayıp yaratmaz — yalnız UPDATE sıklığı artar.
    builder.Services.AddSingleton<ILastSeenThrottle, LastSeenThrottle>();
    // SVCR-001/002/003 follow-up: AssetService'in static field cache'i kalktı.
    // IAssetSymbolIndex singleton — içerik hash imzasıyla snapshot; atomik swap.
    builder.Services.AddSingleton<IAssetSymbolIndex, AssetSymbolIndex>();
    // F5 follow-up: endpoint katmanı repository'ye doğrudan erişmesin diye
    // plan limitlerini çözen service. Sonar S107 ile birlikte handler parametre
    // sayısı 8'den 6'ya iner.
    builder.Services.AddScoped<IPlanLimitResolver, PlanLimitResolver>();

    // ─── GeoIP (IP → ülke/şehir çözümleme) ────────────────────────────────────
    builder.Services.AddSingleton<IGeoIpResolver, MaxMindGeoIpResolver>();

    // ─── Activity Logging (Channel pattern) ───────────────────────────────────
    // DropWrite: kuyruk dolduğunda yeni kayıt düşer ve TryWrite false döner →
    // ChannelActivityLogger bunu warn loglar (telemetri için kritik).
    // DropOldest TryWrite'ın her zaman true dönmesine yol açarak drop sayısını
    // ölçülemez kılıyordu (review C-3 bulgusu).
    var activityChannel = Channel.CreateBounded<ActivityLog>(
        new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    builder.Services.AddSingleton(activityChannel);
    builder.Services.AddSingleton<IActivityLogger, ChannelActivityLogger>();
    builder.Services.AddHostedService<ActivityLogWriter>();
    // IMiddleware ile çalıştığı için transient kayıt zorunlu (UseMiddleware<T>() activate eder).
    builder.Services.AddTransient<ActivityLogMiddleware>();

    // ─── Forwarded Headers (reverse proxy arkasında gerçek IP için) ────────────
    // KnownProxies / KnownNetworks varsayılan olarak yalnızca loopback (127.0.0.1 / ::1)
    // güvenilirdir. Reverse-proxy arkasında çalışan ortamlarda ForwardedHeaders:KnownProxies
    // (CSV IP listesi) veya KnownNetworks (CIDR) ile config'ten ek bilinen subnet'ler eklenir.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        var knownProxies = builder.Configuration["ForwardedHeaders:KnownProxies"];
        if (!string.IsNullOrWhiteSpace(knownProxies))
        {
            foreach (var raw in knownProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IPAddress.TryParse(raw, out var ip))
                    options.KnownProxies.Add(ip);
            }
        }

        var knownNetworks = builder.Configuration["ForwardedHeaders:KnownNetworks"];
        if (!string.IsNullOrWhiteSpace(knownNetworks))
        {
            foreach (var raw in knownNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = raw.Split('/', 2);
                if (parts.Length == 2
                    && IPAddress.TryParse(parts[0], out var prefix)
                    && int.TryParse(parts[1], out var prefixLength))
                {
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
                }
            }
        }

        var forwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit");
        if (forwardLimit.HasValue)
            options.ForwardLimit = forwardLimit.Value;
    });

    // ─── Rate Limiting (IP-bazlı, config-gated — ADR-003) ──────────────────────
    // İki katmanlı throttling modeli:
    //   (1) Cihaz-bazlı GÜNLÜK iş kotası → IDailyLimitGuard (Redis, usage:* key'leri) —
    //       ürün adilliği / kötüye-kullanım (mevcut).
    //   (2) IP-bazlı altyapı throttle → aşağıdaki RateLimiter (in-memory, sabit pencere) —
    //       burst / DoS koruması (yeni).
    // İkisi diktir; ikisi de korunur. Varsayılan KAPALI (RateLimiting:Enabled=false) →
    // mevcut davranışı, local dev'i ve testleri etkilemez; ortam bazında açılır.
    // Dağıtık (çok-instance) Redis-destekli limit, yatay ölçeklenince eklenecek
    // dokümante edilmiş takip işidir (bkz. ADR-003).
    var rateLimitingEnabled = builder.Configuration.GetValue<bool>("RateLimiting:Enabled");
    if (rateLimitingEnabled)
    {
        var permitLimit   = builder.Configuration.GetValue<int?>("RateLimiting:PermitLimit") ?? 100;
        var windowSeconds = builder.Configuration.GetValue<int?>("RateLimiting:WindowSeconds") ?? 60;

        builder.Services.AddRateLimiter(rl =>
        {
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rl.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                // /health ve /metrics throttle dışında (OTel trace filtresiyle tutarlı, gürültü yok).
                var path = httpContext.Request.Path;
                if (path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics"))
                    return RateLimitPartition.GetNoLimiter("infra");

                // İstemci IP'si UseForwardedHeaders SONRASI gerçek IP'dir (KnownProxies yapılandırılmışsa).
                var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window      = TimeSpan.FromSeconds(windowSeconds),
                    QueueLimit  = 0,
                });
            });

            // Reddetme yanıtı DailyLimitExceededExceptionHandler ile aynı RFC 7807 + i18n + traceId
            // şeklini taşır; Retry-After header eklenir.
            rl.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                var localizer = http.RequestServices.GetRequiredService<IStringLocalizer<Saydin.Api.ErrorMessages>>();

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    http.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Type   = "https://saydin.app/errors/rate-limited",
                    Title  = localizer["RateLimited"],
                    Status = StatusCodes.Status429TooManyRequests,
                    Detail = string.Format(localizer["RateLimitedDetail"], windowSeconds, permitLimit),
                    Extensions = { ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier }
                }, (JsonSerializerOptions?)null, "application/problem+json", ct);
            };
        });
    }

    // ─── Build ───────────────────────────────────────────────────────────────
    var app = builder.Build();

    app.UseResponseCompression();
    app.UseForwardedHeaders();

    // ─── Request Localization ──────────────────────────────────────────────────
    var supportedCultures = new[] { new CultureInfo("tr"), new CultureInfo("en") };
    app.UseRequestLocalization(new RequestLocalizationOptions
    {
        DefaultRequestCulture = new RequestCulture("tr"),
        SupportedCultures = supportedCultures,
        SupportedUICultures = supportedCultures,
        ApplyCurrentCultureToResponseHeaders = true
    });

    // UseForwardedHeaders SONRASI (gerçek IP) ve UseRequestLocalization SONRASI (429 mesajı
    // Accept-Language'a göre lokalize) çalışır. Yalnız config açıkken pipeline'a girer.
    if (rateLimitingEnabled)
        app.UseRateLimiter();

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    // Activity log middleware exception handler'dan SONRA, endpoint mapping'den ÖNCE çalışır.
    // Pipeline tamamlandığında builder.StatusCode = Response.StatusCode atanır → 4xx/5xx
    // hatalı isteklerde de activity_logs'a doğru kayıt düşer (review C-3).
    app.UseMiddleware<ActivityLogMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    app.MapPrometheusScrapingEndpoint();
    app.MapHealthChecks("/health");

    app.MapWhatIfEndpoints();
    app.MapDcaEndpoints();
    app.MapAssetsEndpoints();
    app.MapScenariosEndpoints();
    app.MapAppConfigEndpoints();

    Log.Information("Saydin.Api başlatılıyor — ortam: {Environment}", app.Environment.EnvironmentName);
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Saydin.Api beklenmedik şekilde sonlandı");
}
finally
{
    await Log.CloseAndFlushAsync();
}
