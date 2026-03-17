using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using Saydin.PriceIngestion.Adapters;
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
            }));

    // ─── HTTP Clients ────────────────────────────────────────────────────────
    builder.Services
        .AddHttpClient("tcmb", client =>
        {
            client.BaseAddress = new Uri("https://www.tcmb.gov.tr/kurlar/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Saydin/1.0 (+https://saydin.app)");
        })
        .AddStandardResilienceHandler();

    builder.Services
        .AddHttpClient("coingecko", client =>
        {
            client.BaseAddress = new Uri("https://api.coingecko.com/api/v3/");
            client.Timeout = TimeSpan.FromSeconds(30);
            var apiKey = builder.Configuration["ExternalApis:CoinGecko:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Add("x-cg-demo-api-key", apiKey);
        })
        .AddStandardResilienceHandler();

    // GoldAPI pasif — OpenExchangeRates ile değiştirildi. Key geçersizliği nedeniyle devre dışı.
    // builder.Services
    //     .AddHttpClient("goldapi", client =>
    //     {
    //         client.BaseAddress = new Uri("https://www.goldapi.io/api/");
    //         client.Timeout = TimeSpan.FromSeconds(30);
    //         var apiKey = builder.Configuration["ExternalApis:GoldApi:ApiKey"];
    //         if (!string.IsNullOrWhiteSpace(apiKey))
    //             client.DefaultRequestHeaders.Add("x-access-token", apiKey);
    //     })
    //     .AddStandardResilienceHandler();

    builder.Services
        .AddHttpClient("openexchangerates", client =>
        {
            client.BaseAddress = new Uri("https://openexchangerates.org/api/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddStandardResilienceHandler();

    builder.Services
        .AddHttpClient("twelvedata", client =>
        {
            client.BaseAddress = new Uri("https://api.twelvedata.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddStandardResilienceHandler();

    builder.Services
        .AddHttpClient("evds", client =>
        {
            client.BaseAddress = new Uri("https://evds3.tcmb.gov.tr/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Saydin/1.0 (+https://saydin.app)");
        })
        .AddStandardResilienceHandler();

    // ─── EF Core ─────────────────────────────────────────────────────────────
    var pgConnection = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres yapılandırılmamış.");

    builder.Services.AddDbContextFactory<SaydinDbContext>(options =>
        options.UseNpgsql(pgConnection, npgsql =>
            npgsql.MapEnum<AssetCategory>("asset_category"))
               .UseSnakeCaseNamingConvention());

    // ─── Adapters & Repositories ──────────────────────────────────────────────
    builder.Services.AddSingleton<IPriceIngestionRepository, PriceIngestionRepository>();
    builder.Services.AddSingleton<IInflationIngestionRepository, InflationIngestionRepository>();
    builder.Services.AddSingleton<TcmbAdapter>();
    builder.Services.AddSingleton<CoinGeckoAdapter>();
    // builder.Services.AddSingleton<GoldApiAdapter>();  // Pasif
    builder.Services.AddSingleton<OpenExchangeRatesAdapter>();
    builder.Services.AddSingleton<TwelveDataAdapter>();
    builder.Services.AddSingleton<EvdsInflationAdapter>();

    // ─── Workers ─────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<TcmbWorker>();
    builder.Services.AddSingleton<CoinGeckoWorker>();
    // builder.Services.AddSingleton<GoldApiWorker>();  // Pasif
    builder.Services.AddSingleton<OpenExchangeRatesWorker>();
    builder.Services.AddSingleton<TwelveDataWorker>();
    builder.Services.AddSingleton<EvdsInflationWorker>();
    builder.Services.AddHostedService<IngestionOrchestrator>();

    var host = builder.Build();

    Log.Information("Saydin.PriceIngestion başlatılıyor");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Saydin.PriceIngestion beklenmedik şekilde sonlandı");
}
finally
{
    await Log.CloseAndFlushAsync();
}
