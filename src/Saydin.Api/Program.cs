using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using Saydin.Api.Endpoints;
using Saydin.Api.Exceptions;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Diagnostics;
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

    // ─── Exception Handling ──────────────────────────────────────────────────
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<PriceNotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // ─── Health Checks ───────────────────────────────────────────────────────
    builder.Services
        .AddHealthChecks()
        .AddNpgSql(
            builder.Configuration.GetConnectionString("Postgres")!,
            name: "postgresql",
            tags: ["db"])
        .AddRedis(
            builder.Configuration.GetConnectionString("Redis")!,
            name: "redis",
            tags: ["cache"]);

    // ─── OpenAPI ─────────────────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    // ─── Dapper TypeHandlers (global, uygulama başlangıcında) ────────────────
    PriceRepository.RegisterTypeHandlers();

    // ─── Data Access ─────────────────────────────────────────────────────────
    var pgConnection = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres yapılandırılmamış.");

    var redisConnection = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis yapılandırılmamış.");

    builder.Services.AddSingleton<IPriceRepository>(new PriceRepository(pgConnection));
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisConnection));

    // ─── Application Services ────────────────────────────────────────────────
    builder.Services.AddSingleton<IAssetService, AssetService>();
    // builder.Services.AddSingleton<IWhatIfCalculator, WhatIfCalculator>();  ← Faz 1

    // ─── Build ───────────────────────────────────────────────────────────────
    var app = builder.Build();

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
        app.MapOpenApi();

    app.MapPrometheusScrapingEndpoint();
    app.MapHealthChecks("/health");

    // app.MapWhatIfEndpoints();  ← Faz 1: WhatIfCalculator implement edilince açılacak
    app.MapAssetsEndpoints();

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
