using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
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
using Saydin.Api.Runtime;
using Saydin.Api.Security;
using Saydin.Api.Services;
using Saydin.DatabaseSecurity;
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
    var apiRuntime = ApiRuntimeContract.Parse(builder.Configuration, builder.Environment);
    var serviceVersion = ApiServiceVersionContract.Parse(builder.Configuration, builder.Environment);
    builder.WebHost.ConfigureKestrel(apiRuntime.Configure);
    builder.Services.AddSingleton(apiRuntime);
    builder.Services.AddSingleton<Microsoft.AspNetCore.Routing.MatcherPolicy,
        ApiPortEndpointSelectorPolicy>();

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
                    ["service.version"] = serviceVersion
                };
            });
    });

    // ─── OpenTelemetry ───────────────────────────────────────────────────────
    var otlpEndpointUri = new Uri(
        builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService("saydin-api", serviceVersion: serviceVersion)
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
    builder.Services.AddExceptionHandler<RequestBodyTooLargeExceptionHandler>();
    builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
    builder.Services.AddExceptionHandler<FeatureDisabledExceptionHandler>();
    builder.Services.AddExceptionHandler<PriceNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<AssetNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<ScenarioNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<ScenarioLimitExceededExceptionHandler>();
    builder.Services.AddExceptionHandler<DailyLimitExceededExceptionHandler>();
    builder.Services.AddExceptionHandler<QuotaUnavailableExceptionHandler>();
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
    var runtimeDatabase = RuntimeDatabaseOptions.FromEnvironment(
        LoginPurpose.Api, RuntimeDatabasePooling.Service);
    var npgsqlDataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(
        runtimeDatabase,
        dataSource => dataSource.MapEnum<AssetCategory>("asset_category"));
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
            services => services.GetRequiredService<IConnectionMultiplexer>(),
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
    var securityLimiterEnabled = builder.Configuration
        .GetSection(DistributedSecurityLimiterOptions.SectionName)
        .GetValue<bool?>(nameof(DistributedSecurityLimiterOptions.Enabled)) ?? true;
    if (builder.Environment.IsProduction() && !securityLimiterEnabled)
        throw new InvalidOperationException("security_limiter_required_in_production");
    builder.Services.AddDistributedSecurityLimiter(builder.Configuration);

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

    // Installation credentials are bearer secrets. Their HMAC keyring must come
    // from a root-owned/owner-only file; environment variables and config values
    // contain only the non-secret file path and active version.
    var installationCredentialOptions = builder.Configuration
        .GetSection(InstallationCredentialOptions.SectionName)
        .Get<InstallationCredentialOptions>()
        ?? throw new InvalidOperationException("Installation credential configuration is missing.");
    Validator.ValidateObject(
        installationCredentialOptions,
        new ValidationContext(installationCredentialOptions),
        validateAllProperties: true);
    var installationCredentialKeyring = InstallationCredentialKeyring.Load(installationCredentialOptions);
    builder.Services.AddSingleton<IInstallationCredentialKeyring>(installationCredentialKeyring);
    var activityPrincipalPseudonymOptions = builder.Configuration
        .GetSection(ActivityPrincipalPseudonymOptions.SectionName)
        .Get<ActivityPrincipalPseudonymOptions>()
        ?? throw new InvalidOperationException("Activity principal pseudonym configuration is missing.");
    Validator.ValidateObject(
        activityPrincipalPseudonymOptions,
        new ValidationContext(activityPrincipalPseudonymOptions),
        validateAllProperties: true);
    var activityPrincipalPseudonymizer = ActivityPrincipalPseudonymizer.Load(
        activityPrincipalPseudonymOptions);
    builder.Services.AddSingleton<IActivityPrincipalPseudonymizer>(activityPrincipalPseudonymizer);
    builder.Services.AddSingleton<IQuotaSubjectPseudonymizer>(activityPrincipalPseudonymizer);

    // ─── Repositories & Services ─────────────────────────────────────────────
    // F3.1-5 / SVCR-007/025: TimeProvider — servisler DateTime.UtcNow yerine bunu
    // enjekte eder; testlerde FakeTimeProvider ile saat dondurulur (gün dönümü flaky'liği biter).
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<InstallationPrincipalContext>();
    builder.Services.AddScoped<IInstallationPrincipalContext>(
        sp => sp.GetRequiredService<InstallationPrincipalContext>());
    builder.Services.AddScoped<IInstallationRepository, InstallationRepository>();
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
    // DropWrite doluyken item'ı düşürür fakat TryWrite=true döner. Gerçek kayıp yalnız
    // itemDropped callback'inde gözlenir; TryWrite=false completed writer semantiğidir.
    builder.Services.AddSingleton<ActivityLogChannelTelemetry>();
    builder.Services.AddSingleton(sp => Channel.CreateBounded<ActivityLog>(
        new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        },
        sp.GetRequiredService<ActivityLogChannelTelemetry>().RecordDropped));

    builder.Services.AddSingleton<IActivityLogger, ChannelActivityLogger>();
    builder.Services.AddSingleton<IActivityLogBatchStore, EfActivityLogBatchStore>();
    builder.Services.AddHostedService<ActivityLogWriter>();
    // Hosted services stop in reverse registration order. This completion phase
    // therefore closes ingress before ActivityLogWriter is stopped, while the
    // framework's later-registered Kestrel host has already drained requests.
    builder.Services.AddHostedService<ActivityLogChannelLifetime>();
    builder.Services.Configure<HostOptions>(options =>
        options.ShutdownTimeout = TimeSpan.FromSeconds(45));
    // IMiddleware ile çalıştığı için transient kayıt zorunlu (UseMiddleware<T>() activate eder).
    builder.Services.AddTransient<ActivityLogMiddleware>();
    builder.Services.AddTransient<ApiPortBoundaryMiddleware>();

    // ─── Forwarded Headers (reverse proxy arkasında gerçek IP için) ────────────
    // Strict, fail-fast trust contract: malformed/duplicate entries and broad CIDRs
    // are rejected before any listener starts. Framework loopback defaults are cleared.
    builder.Services.Configure<ForwardedHeadersOptions>(apiRuntime.Configure);

    // ─── Build ───────────────────────────────────────────────────────────────
    var app = builder.Build();

    app.UseForwardedHeaders();
    app.UseMiddleware<ApiPortBoundaryMiddleware>();
    app.UseResponseCompression();

    // ─── Request Localization ──────────────────────────────────────────────────
    var supportedCultures = new[] { new CultureInfo("tr"), new CultureInfo("en") };
    app.UseRequestLocalization(new RequestLocalizationOptions
    {
        DefaultRequestCulture = new RequestCulture("tr"),
        SupportedCultures = supportedCultures,
        SupportedUICultures = supportedCultures,
        ApplyCurrentCultureToResponseHeaders = true
    });

    // Middleware sırası (EC-5 + EC-FU ActivityLog status düzeltmesi):
    //   Serilog → ActivityLog → ExceptionHandler → endpoint.
    // İkisi de UseExceptionHandler'ın DIŞINDADIR (önünde) — bu sıralama KRİTİKTİR:
    //  • Serilog: request log'u handler'ın çevirdiği NİHAİ status'ü (403/404/429/502/500)
    //    yansıtır; istisnayı handler 4xx'e çevirmeden gören yanıltıcı "500" artefaktı oluşmaz.
    //    Gerçek 500 exception detayı GlobalExceptionHandler.LogError'da (traceId ile) korunur.
    //  • ActivityLogMiddleware: endpoint exception fırlattığında ExceptionHandler onu 4xx/5xx'e
    //    çevirip yanıtı yazar ve (handler `true` döndüğü için) rethrow ETMEZ → ActivityLog'un
    //    `await next()`'i NORMAL tamamlanır; finally'si ÇEVRİLMİŞ status'ü okuyup activity_logs'a
    //    doğru kodu yazar. (Önceki sıralamada ActivityLog ExceptionHandler'ın İÇİNDEYDİ → finally
    //    response henüz çevrilmeden, StatusCode hâlâ 200 iken çalışıyor ve activity_logs'a yanlış
    //    200 yazıyordu. Regresyon kilidi: ErrorContractHttpTests
    //    `FeatureDisabled_ActivityLog_RecordsConvertedStatus_Not200`.) İç try/catch yalnız
    //    log-gönderim hatasını sarmalar; istek exception'ını YUTMAZ.
    app.UseSerilogRequestLogging();
    app.UseMiddleware<ActivityLogMiddleware>();
    app.UseWhen(context => !ApiPortBoundary.IsAdmissionExempt(context), branch =>
        branch.UseMiddleware<DistributedSecurityLimiterMiddleware>());
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().WithMetadata(
            new ApiEndpointSurfaceMetadata(ApiEndpointSurface.PublicProduct));
        app.MapScalarApiReference().WithMetadata(
            new ApiEndpointSurfaceMetadata(ApiEndpointSurface.PublicProduct));
    }

    app.MapHealthChecks(ApiPortBoundary.LivePath, new()
    {
        Predicate = _ => false,
    }).WithMetadata(new ApiEndpointSurfaceMetadata(ApiEndpointSurface.PublicLiveness));
    app.MapHealthChecks(ApiPortBoundary.ReadyPath, new()
    {
        Predicate = registration => registration.Tags.Contains("db")
                                    || registration.Tags.Contains("cache"),
    }).WithMetadata(new ApiEndpointSurfaceMetadata(ApiEndpointSurface.Management));
    app.MapPrometheusScrapingEndpoint(ApiPortBoundary.MetricsPath)
        .WithMetadata(new ApiEndpointSurfaceMetadata(ApiEndpointSurface.Management));

    var productEndpoints = app.MapGroup("")
        .WithMetadata(new ApiEndpointSurfaceMetadata(ApiEndpointSurface.PublicProduct));
    productEndpoints.MapWhatIfEndpoints();
    productEndpoints.MapDcaEndpoints();
    productEndpoints.MapAssetsEndpoints();
    productEndpoints.MapScenariosEndpoints();
    productEndpoints.MapAppConfigEndpoints();
    productEndpoints.MapInstallationEndpoints();

    SaydinMetrics.InitializeActivityLogContractSeries();
    Log.Information("Saydin.Api başlatılıyor — ortam: {Environment}", app.Environment.EnvironmentName);
    await app.RunAsync();
}
catch (DatabaseSecurityRejectedException exception)
{
    Log.Fatal("Saydin.Api veritabanı güvenlik sınırında reddedildi: {Code}", exception.Code);
    Environment.ExitCode = 78;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Saydin.Api beklenmedik şekilde sonlandı");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
