using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
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
using Saydin.Api.Endpoints;
using Saydin.Api.Exceptions;
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
            .AddMeter(SaydinMetrics.WhatIfCalculations.Meter.Name)
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
    builder.Services.AddLocalization();

    // ─── Exception Handling ──────────────────────────────────────────────────
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<PriceNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<AssetNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<ScenarioNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<ScenarioLimitExceededExceptionHandler>();
    builder.Services.AddExceptionHandler<DailyLimitExceededExceptionHandler>();
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
    builder.Services.AddDbContext<SaydinDbContext>(options =>
        options.UseNpgsql(npgsqlDataSource, npgsql =>
            npgsql.MapEnum<AssetCategory>("asset_category"))
               .UseSnakeCaseNamingConvention());

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

    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisOptions));

    // ─── Response Compression ────────────────────────────────────────────────
    builder.Services.AddResponseCompression(opts => opts.EnableForHttps = true);

    // ─── Options ─────────────────────────────────────────────────────────────
    builder.Services.Configure<PlanOptions>(
        builder.Configuration.GetSection(PlanOptions.SectionName));

    // ─── Repositories & Services ─────────────────────────────────────────────
    builder.Services.AddScoped<IPriceRepository, PriceRepository>();
    builder.Services.AddScoped<IAssetService, AssetService>();
    builder.Services.AddScoped<IInflationRepository, InflationRepository>();
    builder.Services.AddScoped<IWhatIfCalculator, WhatIfCalculator>();
    builder.Services.AddScoped<IDcaCalculator, DcaCalculator>();
    builder.Services.AddScoped<ISavedScenarioRepository, SavedScenarioRepository>();
    builder.Services.AddScoped<ISavedScenarioService, SavedScenarioService>();

    // ─── Build ───────────────────────────────────────────────────────────────
    var app = builder.Build();

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

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

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
