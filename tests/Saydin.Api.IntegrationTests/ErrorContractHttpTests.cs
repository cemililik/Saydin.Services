using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Saydin.DatabaseSecurity;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Models.Responses;
using Saydin.Shared.Data;
using StackExchange.Redis;

namespace Saydin.Api.IntegrationTests;

/// <summary>
/// EC-2: Gerçek HTTP-sınırı üzerinden tüm pipeline'ı (installation auth filter →
/// servis → IExceptionHandler zinciri → UseExceptionHandler → problem+json yazımı)
/// çalıştıran <see cref="WebApplicationFactory{TEntryPoint}"/>. Program.cs
/// Exact managed DB topology/file-secret veya Redis yapılandırılmamışsa fail-fast attığından, gerçek
/// compose PG/Redis erişilebilir değilse yerel optional modda testler
/// <c>SkippableFact</c> ile atlanır. Required CI modunda güvenli DB guard veya infra
/// probe hatası constructor'da fail-fast olur.
/// GeoIP veritabanı test ortamında yoktur — <see cref="Saydin.Api.Services.MaxMindGeoIpResolver"/>
/// bunu null-safe karşılar (mock GEREKMEZ; CLAUDE.md "DB/Redis mock yasak" kuralı korunur).
/// </summary>
public class ErrorContractWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string? keyringDirectory;
    private readonly string? keyringFile;
    private readonly string? pseudonymDirectory;
    private readonly string? pseudonymFile;
    private readonly bool ownsKeyringFile;

    public bool InfraAvailable { get; }
    public string SkipReason { get; }
    protected string SecuritySecretFile => keyringFile ?? throw new InvalidOperationException(
        "HTTP integration keyring was not provisioned.");

    public ErrorContractWebAppFactory()
    {
        // Bulgu 7 (re-raised): env VARLIĞI yeterli değil — gerçek ERİŞİLEBİLİRLİK probe edilir
        // (DatabaseFixture/RedisFixture deseniyle birebir). Env set ama altyapı erişilemezse
        // (compose dışı koşu / yarış) artık CreateClient() boot'unda belirsiz hata yerine test
        // temiz biçimde Skip olur. Tüm exception'lar "erişilemez" sayılır, dışarı sızmaz.
        RuntimeDatabaseOptions? pg = null;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGHOST")))
            pg = RuntimeDatabaseOptions.FromEnvironment(
                LoginPurpose.Api, RuntimeDatabasePooling.Service);
        var redis = Environment.GetEnvironmentVariable("ConnectionStrings__Redis");

        // Required modda connection açılmadan önce run-id/DB güvenlik sınırı uygulanır.
        if (pg is not null)
            IntegrationTestEnvironment.ValidateRequiredDatabase(pg.Host, pg.Database);
        else if (IntegrationTestEnvironment.IsRequired)
            throw new InvalidOperationException("Required managed PostgreSQL topology missing.");
        IntegrationTestEnvironment.ValidateRequiredRedis(redis);

        (InfraAvailable, SkipReason) = ProbeInfra(pg, redis);
        if (IntegrationTestEnvironment.IsRequired && !InfraAvailable)
            throw new InvalidOperationException(
                $"Required integration HTTP altyapısı hazır değil; testler skip edilemez: {SkipReason}");

        if (InfraAvailable)
        {
            if (!OperatingSystem.IsLinux())
                throw new InvalidOperationException("HTTP integration secret contract requires Linux.");

            pseudonymDirectory = Path.Combine(
                Path.GetTempPath(), $"saydin-http-pseudonym-{Guid.NewGuid():N}");
            Directory.CreateDirectory(pseudonymDirectory);
            File.SetUnixFileMode(pseudonymDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            pseudonymFile = Path.Combine(pseudonymDirectory, "activity-principal-hmac");
            var pseudonymKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllBytes(pseudonymFile, pseudonymKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pseudonymKey);
            }
            File.SetUnixFileMode(pseudonymFile,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var externallyProvisioned = Environment.GetEnvironmentVariable(
                "SAYDIN_HTTP_TEST_KEYRING_FILE");
            if (IntegrationTestEnvironment.IsRequired)
            {
                if (string.IsNullOrWhiteSpace(externallyProvisioned)
                    || !Path.IsPathFullyQualified(externallyProvisioned))
                    throw new InvalidOperationException(
                        "Required HTTP integration keyring secret is missing.");
                keyringFile = externallyProvisioned;
                return;
            }

            keyringDirectory = Path.Combine(
                Path.GetTempPath(), $"saydin-http-keyring-{Guid.NewGuid():N}");
            Directory.CreateDirectory(keyringDirectory);
            File.SetUnixFileMode(keyringDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            keyringFile = Path.Combine(keyringDirectory, "keyring.json");
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(keyringFile, JsonSerializer.Serialize(
                    new Dictionary<string, string> { ["1"] = Convert.ToBase64String(key) }));
                File.SetUnixFileMode(keyringFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                ownsKeyringFile = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>
    /// PG + Redis erişilebilirliğini kısa timeout'la dener; ikisi de bağlanırsa
    /// <c>(true, "")</c>, değilse <c>(false, sebep)</c> döner. Her hata yutulur — probe
    /// asla fırlatmaz (CreateClient() çağrılmadan önce constructor'da güvenle çalışır).
    /// </summary>
    private static (bool Available, string SkipReason) ProbeInfra(
        RuntimeDatabaseOptions? pg, string? redis)
    {
        if (pg is null || string.IsNullOrWhiteSpace(redis))
            return (false, "Managed PostgreSQL/Redis config yok (compose `tests` profili gerekli).");

        try
        {
            using var dataSource = RuntimeDatabase.OpenVerifiedDataSourceAsync(pg)
                .GetAwaiter().GetResult();
            using var conn = dataSource.OpenConnection();
        }
        catch (Exception ex)
        {
            return (false, $"PostgreSQL erişilemez: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var opts = ConfigurationOptions.Parse(redis);
            opts.AbortOnConnectFail = false;
            opts.ConnectTimeout = 3000;
            using var mux = ConnectionMultiplexer.Connect(opts);
            if (!mux.IsConnected)
                return (false, "Redis bağlantısı kurulamadı (IsConnected=false).");
        }
        catch (Exception ex)
        {
            return (false, $"Redis erişilemez: {ex.GetType().Name}: {ex.Message}");
        }

        return (true, string.Empty);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development: OpenAPI/Scalar map edilir ama hata-sözleşmesi davranışı prod ile aynıdır.
        // Dağıtık admission'ın kendi gerçek-Redis testleri ayrıdır; bu factory hata sözleşmesini izole eder.
        builder.UseEnvironment("Development");
        // Program reads these fail-fast contracts before builder.Build(). Host
        // settings are therefore authoritative for WebApplicationFactory's
        // deferred minimal-host bootstrap; the config provider below remains the
        // normal application-options view after the host has been constructed.
        builder.UseSetting("AllowedHosts", "localhost;127.0.0.1;[::1]");
        builder.UseSetting("ApiRuntime:PublicPort", "8080");
        builder.UseSetting("ApiRuntime:ManagementPort", "9090");
        builder.UseSetting("ForwardedHeaders:KnownProxies", "127.0.0.1,::1");
        builder.UseSetting("ForwardedHeaders:KnownNetworks", string.Empty);
        builder.UseSetting("ForwardedHeaders:ForwardLimit", "1");
        builder.UseSetting(
            "InstallationCredentials:SecretFile",
            keyringFile ?? throw new InvalidOperationException(
                "HTTP integration keyring was not provisioned."));
        builder.UseSetting("InstallationCredentials:ActiveKeyVersion", "1");
        builder.UseSetting(
            "ActivityPrincipalPseudonym:SecretFile",
            pseudonymFile ?? throw new InvalidOperationException(
                "HTTP integration activity principal secret was not provisioned."));
        builder.UseSetting("DistributedSecurityLimiter:Enabled", "false");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "localhost;127.0.0.1;[::1]",
                ["ApiRuntime:PublicPort"] = "8080",
                ["ApiRuntime:ManagementPort"] = "9090",
                ["ForwardedHeaders:KnownProxies"] = "127.0.0.1,::1",
                ["ForwardedHeaders:KnownNetworks"] = "",
                ["ForwardedHeaders:ForwardLimit"] = "1",
                ["InstallationCredentials:SecretFile"] = keyringFile,
                ["InstallationCredentials:ActiveKeyVersion"] = "1",
                ["ActivityPrincipalPseudonym:SecretFile"] = pseudonymFile,
                ["DistributedSecurityLimiter:Enabled"] = "false",
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !ownsKeyringFile || keyringFile is null || keyringDirectory is null)
        {
            if (pseudonymFile is not null && File.Exists(pseudonymFile)) File.Delete(pseudonymFile);
            if (pseudonymDirectory is not null && Directory.Exists(pseudonymDirectory))
                Directory.Delete(pseudonymDirectory);
            return;
        }
        if (File.Exists(keyringFile)) File.Delete(keyringFile);
        if (Directory.Exists(keyringDirectory)) Directory.Delete(keyringDirectory);
        if (pseudonymFile is not null && File.Exists(pseudonymFile)) File.Delete(pseudonymFile);
        if (pseudonymDirectory is not null && Directory.Exists(pseudonymDirectory))
            Directory.Delete(pseudonymDirectory);
    }
}

public sealed class SecurityAdmissionWebAppFactory : ErrorContractWebAppFactory
{
    public string RedisKeyPrefix { get; } = $"securityhttpitest{Guid.NewGuid():N}:";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var settings = new Dictionary<string, string?>
        {
            ["DistributedSecurityLimiter:Enabled"] = "true",
            ["DistributedSecurityLimiter:WindowSeconds"] = "60",
            ["DistributedSecurityLimiter:ExactIpLimit"] = "100",
            ["DistributedSecurityLimiter:NetworkLimit"] = "100",
            ["DistributedSecurityLimiter:PrincipalLimit"] = "1",
            ["DistributedSecurityLimiter:RegistrationExactHourlyLimit"] = "10",
            ["DistributedSecurityLimiter:RegistrationExactDailyLimit"] = "10",
            ["DistributedSecurityLimiter:RegistrationNetworkHourlyLimit"] = "10",
            ["DistributedSecurityLimiter:RegistrationNetworkDailyLimit"] = "10",
            ["DistributedSecurityLimiter:CalculationNetworkDailyLimit"] = "100",
            ["DistributedSecurityLimiter:HmacKeyFile"] = SecuritySecretFile,
            ["DistributedSecurityLimiter:RedisKeyPrefix"] = RedisKeyPrefix,
        };
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, NullRemoteIpLoopbackStartupFilter>());
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(settings));
    }
}

internal sealed class NullRemoteIpLoopbackStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
            await pipeline(context);
        });
        next(app);
    };
}

[Collection(DatabaseCollection.Name)]
public sealed class SecurityAdmissionHttpTests(
    SecurityAdmissionWebAppFactory factory,
    DatabaseFixture database)
    : IClassFixture<SecurityAdmissionWebAppFactory>
{
    [SkippableFact]
    public async Task PrincipalLimit_WithRealRedis_ReturnsLocalized429BeforeHandler()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        Skip.IfNot(await ErrorContractHttpTests.ApiTrustReadyAsync(database),
            "Migration 023 API admission contract is required.");
        var client = factory.CreateClient();

        try
        {
            var installation = await ErrorContractHttpTests.RegisterAsync(client);
            using var first = CalculationRequest(installation.Credential, "en");
            using var firstResponse = await client.SendAsync(first);
            firstResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the first admitted request must reach the free-tier handler");

            using var second = CalculationRequest(installation.Credential, "tr");
            using var secondResponse = await client.SendAsync(second);
            secondResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            secondResponse.Content.Headers.ContentType!.MediaType.Should()
                .Be("application/problem+json");
            secondResponse.Headers.RetryAfter.Should().NotBeNull();
            var problem = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
            problem.GetProperty("code").GetString().Should().Be("security_rate_limited");
            problem.GetProperty("title").GetString().Should().Be("Çok fazla istek");
        }
        finally
        {
            await DeleteRedisKeysAsync(factory.RedisKeyPrefix);
        }
    }

    private static HttpRequestMessage CalculationRequest(string credential, string language)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/what-if/calculate")
        {
            Content = JsonContent.Create(new
            {
                assetSymbol = "USDTRY",
                buyDate = "2000-01-01",
                amount = 1000m,
                amountType = "try",
            }),
        };
        ErrorContractHttpTests.Authorize(request, credential);
        request.Headers.TryAddWithoutValidation("Accept-Language", language);
        return request;
    }

    private static async Task DeleteRedisKeysAsync(string prefix)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Redis")
            ?? throw new InvalidOperationException("Redis integration connection is missing.");
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(connection);
        var server = multiplexer.GetServer(multiplexer.GetEndPoints().Single());
        var keys = server.Keys(pattern: $"{prefix}*").ToArray();
        if (keys.Length > 0)
            await multiplexer.GetDatabase().KeyDeleteAsync(keys);
    }
}

// HTTP factory class-scoped kalır. Database collection fixture yalnız owner-only assertion/cleanup
// sorgularını SUT managed API datasource'undan ayırır; test principal kimlikleri yine benzersizdir.
[Collection(DatabaseCollection.Name)]
public sealed class ErrorContractHttpTests(
    ErrorContractWebAppFactory factory,
    DatabaseFixture database)
    : IClassFixture<ErrorContractWebAppFactory>
{
    private const string CalculatePath = "/v1/what-if/calculate";
    private const string ProblemJson   = "application/problem+json";

    /// <summary>Geçerli ama 12 aydan eski BuyDate (free tier `extended_history` 403'ünü tetikler).</summary>
    private static object OldBuyDatePayload() => new
    {
        assetSymbol = "USDTRY",
        buyDate     = "2000-01-01",
        amount      = 1000m,
        amountType  = "try",
    };

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [SkippableFact]
    public async Task ScenarioPage_InvalidLimit_ReturnsValidationProblemInsteadOfEmptyBinder400()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        Skip.IfNot(await ApiTrustReadyAsync(database), "Migration 023 API trust contract is required.");
        var client = factory.CreateClient();
        var installation = await RegisterAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/scenarios/page?limit=not-an-integer");
        Authorize(request, installation.Credential);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ProblemJson);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("code").GetString().Should().Be("validation");
        problem.GetProperty("field").GetString().Should().Be("limit");
    }

    [SkippableFact]
    public async Task ScenarioPage_EmptyCursor_IsTheFirstPage()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        Skip.IfNot(await ApiTrustReadyAsync(database), "Migration 023 API trust contract is required.");
        var client = factory.CreateClient();
        var installation = await RegisterAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/scenarios/page?cursor=");
        Authorize(request, installation.Credential);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<ScenarioPageResponse>();
        page.Should().NotBeNull();
        page!.Items.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task MissingInstallationCredential_ReturnsGeneric401Problem()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(CalculatePath, OldBuyDatePayload());

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType!.MediaType.Should().Be(ProblemJson);
        resp.Headers.WwwAuthenticate.Should().ContainSingle(value => value.Scheme == "Installation");

        var root = await ReadProblemAsync(resp);
        root.GetProperty("type").GetString().Should()
            .Be("https://saydin.app/errors/invalid-installation-credential");
        root.GetProperty("code").GetString().Should().Be("invalid_installation_credential");
        root.TryGetProperty("traceId", out var traceId).Should().BeTrue();
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task LegacyDeviceHeaderAlone_CannotAuthorizePrivateEndpoint()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, CalculatePath)
        {
            Content = JsonContent.Create(OldBuyDatePayload()),
        };
        req.Headers.TryAddWithoutValidation("X-Device-ID", "legacy-header-is-not-authority");

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType!.MediaType.Should().Be(ProblemJson);

        var root = await ReadProblemAsync(resp);
        root.GetProperty("type").GetString().Should()
            .Be("https://saydin.app/errors/invalid-installation-credential");
        root.GetProperty("code").GetString().Should().Be("invalid_installation_credential");
        root.TryGetProperty("traceId", out var traceId).Should().BeTrue();
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task FeatureDisabled_ExtendedHistory_Returns403_ProblemJson_WithCodeAndFeature()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        Skip.IfNot(await ApiTrustReadyAsync(database), "Migration 021 API trust contract is required.");
        var client = factory.CreateClient();
        var installation = await RegisterAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, CalculatePath)
        {
            Content = JsonContent.Create(OldBuyDatePayload()),
        };
        Authorize(req, installation.Credential);

        var resp = await client.SendAsync(req);

        // Yeni registration principal'ı free tier'dır; PriceHistoryMonths=12 ve 2000 yılı pencere dışıdır.
        // Bulgu 5: 403 beklentisi, history-gate'in (WhatIfCalculator.EnsureWithinHistoryWindow)
        // price-lookup'tan ÖNCE çalışmasına bağlıdır; bu sıra bozulursa price-not-found (404)
        // alınır (USDTRY için 2000 yılında seed fiyat yoktur). Sıra korunmalı.
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
        Skip.IfNot(await ApiTrustReadyAsync(database), "Migration 021 API trust contract is required.");
        var client = factory.CreateClient();
        var installation = await RegisterAsync(client);

        async Task<string> GetTitleAsync(string acceptLanguage)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, CalculatePath)
            {
                Content = JsonContent.Create(OldBuyDatePayload()),
            };
            Authorize(req, installation.Credential);
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

    /// <summary>
    /// Bulgu 1 (regresyon kilidi): Endpoint exception fırlatan bir istekte activity_logs satırı
    /// istemciye giden ÇEVRİLMİŞ status'ü (403) yazmalı — varsayılan 200'ü DEĞİL. Bu, middleware
    /// sırasının `Serilog → ActivityLog → ExceptionHandler → endpoint` olmasını doğrular:
    /// ActivityLogMiddleware UseExceptionHandler'ın DIŞINDA olduğundan, handler exception'ı 403'e
    /// çevirip rethrow etmeden döndükten SONRA finally'si çalışır ve doğru status'ü okur. (Önceki
    /// sıralamada — ActivityLog ExceptionHandler'ın içinde — bu satır 200 yazıyordu.)
    /// activity_logs arka plan ActivityLogWriter ile asenkron yazıldığından satır poll edilir.
    /// </summary>
    [SkippableFact]
    public async Task FeatureDisabled_ActivityLog_RecordsConvertedStatus_Not200()
    {
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        Skip.IfNot(await ApiTrustReadyAsync(database), "Migration 021 API trust contract is required.");
        var client = factory.CreateClient();
        var installation = await RegisterAsync(client);

        var req = new HttpRequestMessage(HttpMethod.Post, CalculatePath)
        {
            Content = JsonContent.Create(OldBuyDatePayload()),
        };
        Authorize(req, installation.Credential);

        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Arka plan writer satırı batch'leyerek yazar; ~15 sn poll (compose ağında ms mertebesi).
        short? recordedStatus = null;
        for (var attempt = 0; attempt < 75 && recordedStatus is null; attempt++)
        {
            await using var db = database.CreateAdminContext();
            recordedStatus = await db.ActivityLogs
                .Where(a => a.UserId == installation.PrincipalId)
                .Select(a => (short?)a.StatusCode)
                .FirstOrDefaultAsync();
            if (recordedStatus is null)
                await Task.Delay(200);
        }

        try
        {
            recordedStatus.Should().NotBeNull(
                "endpoint handler activity log builder'ı oluşturdu (calculate ilk satır) → satır yazılmalı");
            recordedStatus.Should().Be((short)HttpStatusCode.Forbidden,
                "activity_logs istemciye giden çevrilmiş status'ü (403) yazmalı, varsayılan 200'ü değil (Bulgu 1)");
        }
        finally
        {
            // Paylaşılan activity tablosunu kirletme — bu testin satır(lar)ını sil.
            // Principal deletion remains migration 022 work: Timescale's compressed-hypertable
            // RI path cannot currently execute users -> activity_logs SET NULL safely.
            await using var db = database.CreateAdminContext();
            await db.ActivityLogs.Where(a => a.UserId == installation.PrincipalId).ExecuteDeleteAsync();
        }
    }

    internal static async Task<InstallationRegistrationResponse> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/v1/installations", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        return (await response.Content.ReadFromJsonAsync<InstallationRegistrationResponse>())!;
    }

    internal static void Authorize(HttpRequestMessage request, string credential) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Installation", credential);

    internal static async Task<bool> ApiTrustReadyAsync(DatabaseFixture database)
    {
        if (!database.ApiTrust)
            return false;
        await using var context = database.CreateAdminContext();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT pg_catalog.to_regprocedure(
                       'public.register_installation(uuid,uuid,bytea,smallint)') IS NOT NULL
               AND pg_catalog.to_regprocedure(
                       'public.resolve_installation_rotation_commit(uuid,bytea,smallint)') IS NOT NULL
            """;
        return await command.ExecuteScalarAsync() is true;
    }
}
