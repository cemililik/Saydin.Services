using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.PriceIngestion.Workers;
using Saydin.Shared.Diagnostics;

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
        .AddStandardResilienceHandler();   // Polly: retry + circuit-breaker + timeout

    // ─── Adapters & Repositories ──────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres yapılandırılmamış.");

    builder.Services.AddSingleton<IExternalPriceAdapter, TcmbAdapter>();
    builder.Services.AddSingleton<IPriceIngestionRepository>(
        new PriceIngestionRepository(connectionString));

    // ─── Workers ─────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<TcmbWorker>();
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
