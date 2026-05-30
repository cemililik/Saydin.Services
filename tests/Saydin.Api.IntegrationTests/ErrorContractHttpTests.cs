using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Saydin.Api.IntegrationTests;

/// <summary>
/// EC-2: Gerçek HTTP-sınırı üzerinden tüm pipeline'ı (RequireDeviceId filter →
/// servis → IExceptionHandler zinciri → UseExceptionHandler → problem+json yazımı)
/// çalıştıran <see cref="WebApplicationFactory{TEntryPoint}"/>. Program.cs
/// ConnectionStrings:Postgres/Redis yapılandırılmamışsa fail-fast attığından, gerçek
/// compose PG/Redis erişilebilir değilse testler <c>SkippableFact</c> ile atlanır
/// (kırmızıya dönmez; mevcut IntegrationTests deseniyle tutarlı).
/// GeoIP veritabanı test ortamında yoktur — <see cref="Saydin.Api.Services.MaxMindGeoIpResolver"/>
/// bunu null-safe karşılar (mock GEREKMEZ; CLAUDE.md "DB/Redis mock yasak" kuralı korunur).
/// </summary>
public sealed class ErrorContractWebAppFactory : WebApplicationFactory<Program>
{
    public bool InfraAvailable { get; }
    public string SkipReason { get; }

    public ErrorContractWebAppFactory()
    {
        var pg    = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        var redis = Environment.GetEnvironmentVariable("ConnectionStrings__Redis");
        InfraAvailable = !string.IsNullOrWhiteSpace(pg) && !string.IsNullOrWhiteSpace(redis);
        SkipReason = InfraAvailable
            ? string.Empty
            : "ConnectionStrings__Postgres/__Redis env yok (compose `tests` profili gerekli).";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development: OpenAPI/Scalar map edilir ama hata-sözleşmesi davranışı prod ile aynıdır
        // (UseDeveloperExceptionPage YOK). RateLimiting kapalı tutulur (testin niyeti dışında).
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "false",
            }));
    }
}

[Collection("error-contract-http")]
public sealed class ErrorContractHttpTests(ErrorContractWebAppFactory factory)
    : IClassFixture<ErrorContractWebAppFactory>
{
    private const string CalculatePath = "/v1/what-if/calculate";
    private const string ProblemJson   = "application/problem+json";

    /// <summary>Geçerli ama 12 aydan eski BuyDate (free tier `extended_history` 403'ünü tetikler).</summary>
    private static object OldBuyDatePayload() => new
    {
        assetSymbol = "USDTRY",
        buyDate     = "2000-01-01",
        amount      = 1000,
        amountType  = "try",
    };

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [SkippableFact]
    public async Task MissingDeviceId_Returns400_ProblemJson_WithStableCode()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        var client = factory.CreateClient();

        // X-Device-ID header YOK → RequireDeviceId filter reddetmeli (servise hiç inilmez).
        var resp = await client.PostAsJsonAsync(CalculatePath, OldBuyDatePayload());

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType!.MediaType.Should().Be(ProblemJson);

        var root = await ReadProblemAsync(resp);
        root.GetProperty("type").GetString().Should().Be("https://saydin.app/errors/missing-device-id");
        root.GetProperty("code").GetString().Should().Be("missing_device_id");
    }

    [SkippableFact]
    public async Task InvalidDeviceId_Returns400_ProblemJson_WithStableCode()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, CalculatePath)
        {
            Content = JsonContent.Create(OldBuyDatePayload()),
        };
        req.Headers.TryAddWithoutValidation("X-Device-ID", "bad id with spaces!");  // regex ihlali

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType!.MediaType.Should().Be(ProblemJson);

        var root = await ReadProblemAsync(resp);
        root.GetProperty("type").GetString().Should().Be("https://saydin.app/errors/invalid-device-id");
        root.GetProperty("code").GetString().Should().Be("invalid_device_id");
    }

    [SkippableFact]
    public async Task FeatureDisabled_ExtendedHistory_Returns403_ProblemJson_WithCodeAndFeature()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, CalculatePath)
        {
            Content = JsonContent.Create(OldBuyDatePayload()),
        };
        req.Headers.TryAddWithoutValidation("X-Device-ID", $"itest-{Guid.NewGuid():N}");

        var resp = await client.SendAsync(req);

        // Free tier (bilinmeyen cihaz → null user) PriceHistoryMonths=12; 2000 yılı pencere dışı.
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        resp.Content.Headers.ContentType!.MediaType.Should().Be(ProblemJson);

        var root = await ReadProblemAsync(resp);
        root.GetProperty("type").GetString().Should().Be("https://saydin.app/errors/feature-disabled");
        root.GetProperty("code").GetString().Should().Be("feature_disabled");
        root.GetProperty("feature").GetString().Should().Be("extended_history");
        root.TryGetProperty("traceId", out var traceId).Should().BeTrue();
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task FeatureDisabled_LocalizesTitle_ByAcceptLanguage()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        var client = factory.CreateClient();

        async Task<string> GetTitleAsync(string acceptLanguage)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, CalculatePath)
            {
                Content = JsonContent.Create(OldBuyDatePayload()),
            };
            req.Headers.TryAddWithoutValidation("X-Device-ID", $"itest-{Guid.NewGuid():N}");
            req.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
            var resp = await client.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var root = await ReadProblemAsync(resp);
            return root.GetProperty("title").GetString()!;
        }

        var tr = await GetTitleAsync("tr");
        var en = await GetTitleAsync("en");

        tr.Should().NotBeNullOrWhiteSpace();
        en.Should().NotBeNullOrWhiteSpace();
        tr.Should().NotBe(en, "title Accept-Language'e göre lokalize olmalı (tr ≠ en)");
    }
}
