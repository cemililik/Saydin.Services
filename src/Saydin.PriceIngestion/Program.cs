using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using Saydin.DatabaseSecurity;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Extensions;
using Saydin.PriceIngestion.Repositories;
using Saydin.PriceIngestion.Workers;
using Saydin.Shared.Data;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    // Provider credentials are validated before DB authentication, window planning,
    // hosted-service construction or any outbound HTTP can occur.
    ProviderStartupValidator.Validate(builder.Configuration);

    // ─── Serilog ─────────────────────────────────────────────────────────────
    builder.Services.AddSerilog((services, cfg) =>
    {
        var otlpEndpoint = builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317";

        cfg
            .ReadFrom.Configuration(builder.Configuration)
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
                    ["service.name"]    = "saydin-price-ingestion",
                    ["service.version"] = "1.0.0"
                };
            });
    });

    // ─── OpenTelemetry ───────────────────────────────────────────────────────
    var otlpEndpointUri = new Uri(builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("saydin-price-ingestion", serviceVersion: "1.0.0"))
        .WithTracing(tracing => tracing
            .AddSource(SaydinActivitySource.Instance.Name)
            .AddHttpClientInstrumentation(opts => opts.RecordException = true)
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = otlpEndpointUri;
                opts.Protocol = OtlpExportProtocol.Grpc;
            }))
        .WithMetrics(metrics => metrics
            // EVDS ingestion failure metric'i + future business counter'lar burada toplanır.
            // Shared meter adı SaydinMetrics.MeterName'den geliyor — copy/paste hatası riski yok.
            .AddMeter(SaydinMetrics.MeterName)
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = otlpEndpointUri;
                opts.Protocol = OtlpExportProtocol.Grpc;
            }));

    // ─── HTTP Clients ────────────────────────────────────────────────────────
    builder.Services
        .AddHttpClient("tcmb", client =>
        {
            client.BaseAddress = new Uri("https://www.tcmb.gov.tr/kurlar/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Saydin/1.0 (+https://saydin.app)");
        })
        .AddSaydinResilience();

    builder.Services
        .AddHttpClient("coingecko", client =>
        {
            client.BaseAddress = new Uri("https://api.coingecko.com/api/v3/");
            client.Timeout = TimeSpan.FromSeconds(30);
            var apiKey = builder.Configuration["ExternalApis:CoinGecko:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Add("x-cg-demo-api-key", apiKey);
        })
        .AddSaydinResilience();

    builder.Services
        .AddHttpClient("openexchangerates", client =>
        {
            client.BaseAddress = new Uri("https://openexchangerates.org/api/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddSaydinResilience();

    builder.Services
        .AddHttpClient("twelvedata", client =>
        {
            client.BaseAddress = new Uri("https://api.twelvedata.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddSaydinResilience();

    builder.Services
        .AddHttpClient("evds", client =>
        {
            client.BaseAddress = new Uri("https://evds3.tcmb.gov.tr/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Saydin/1.0 (+https://saydin.app)");
        })
        .AddSaydinResilience();

    // ─── EF Core ─────────────────────────────────────────────────────────────
    var runtimeDatabase = RuntimeDatabaseOptions.FromEnvironment(
        LoginPurpose.Ingestion, RuntimeDatabasePooling.Service);
    var npgsqlDataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(
        runtimeDatabase,
        dataSource => dataSource.MapEnum<AssetCategory>("asset_category"));

    builder.Services.AddDbContextFactory<SaydinDbContext>(options =>
        options.UseNpgsql(npgsqlDataSource, npgsql =>
            npgsql.MapEnum<AssetCategory>("asset_category"))
               .UseSnakeCaseNamingConvention());
    builder.Services.AddSingleton(npgsqlDataSource);

    // ─── Adapters & Repositories ──────────────────────────────────────────────
    builder.Services.AddSingleton<IPriceIngestionRepository, PriceIngestionRepository>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IProcessExitCodeSink, EnvironmentProcessExitCodeSink>();
    builder.Services.AddSingleton<IIngestionPersistenceFaultInjector, NoopIngestionPersistenceFaultInjector>();
    builder.Services.AddSingleton<IIngestionFreshnessTelemetry, IngestionFreshnessTelemetry>();
    builder.Services.AddSingleton<IIngestionWindowRepository, IngestionWindowRepository>();
    builder.Services.AddSingleton<TcmbAdapter>();
    builder.Services.AddSingleton<CoinGeckoAdapter>();
    builder.Services.AddSingleton<OpenExchangeRatesAdapter>();
    builder.Services.AddSingleton<TwelveDataAdapter>();
    builder.Services.AddSingleton<IInflationAdapter, EvdsInflationAdapter>();

    // ─── Workers ─────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<TcmbWorker>();
    builder.Services.AddSingleton<CoinGeckoWorker>();
    builder.Services.AddSingleton<OpenExchangeRatesWorker>();
    builder.Services.AddSingleton<TwelveDataWorker>();
    builder.Services.AddSingleton<EvdsInflationWorker>();
    builder.Services.AddHostedService<IngestionFreshnessHydrationService>();
    builder.Services.AddHostedService<IngestionOrchestrator>();
    builder.Services.AddHostedService<Saydin.PriceIngestion.BackgroundServices.LivenessHeartbeatService>();
    builder.Services.Configure<HostOptions>(options =>
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

    var host = builder.Build();

    Log.Information("Saydin.PriceIngestion başlatılıyor");
    await host.RunAsync();
}
catch (DatabaseSecurityRejectedException exception)
{
    Log.Fatal("Saydin.PriceIngestion veritabanı güvenlik sınırında reddedildi: {Code}", exception.Code);
    Environment.ExitCode = 78;
}
catch (ProviderStartupRejectedException exception)
{
    Log.Fatal("Saydin.PriceIngestion provider startup contract rejected: {Code}", exception.Code);
    Environment.ExitCode = 78;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Saydin.PriceIngestion beklenmedik şekilde sonlandı");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
