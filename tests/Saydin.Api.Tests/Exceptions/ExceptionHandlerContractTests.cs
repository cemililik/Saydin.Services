using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Saydin.Api;
using Saydin.Api.Exceptions;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Tests.Exceptions;

/// <summary>
/// EC-3 / EC-4 / EC-9 sözleşme kilidi: her <see cref="IExceptionHandler"/>'ın ürettiği RFC 7807
/// gövdesini <b>handler seviyesinde</b> (altyapısız, deterministik) doğrular. HTTP-sınırı
/// (WebApplicationFactory) testleri gerçek PG/Redis ister ve <c>SkippableFact</c> ile atlanabilir;
/// bu testler ise her CI koşusunda çalışır → kontrat regresyonu (yanlış content-type, kayıp
/// <c>code</c>, sızan teknik mesaj) burada yakalanır.
///
/// Doğrulanan değişmezler:
/// <list type="bullet">
///   <item><b>Content-Type = application/problem+json</b> (EC-4).</item>
///   <item><b>Extensions["code"]</b> kararlı, lokalden bağımsız makine kodu (EC-3).</item>
///   <item>Her yanıt <c>traceId</c> taşır; <c>title</c> lokalize çözülür (ham key değil).</item>
///   <item>GlobalExceptionHandler teknik mesajı/stack'i gövdeye <b>sızdırmaz</b>.</item>
///   <item>ExternalApiExceptionHandler upstream <c>source</c>'u gövdeye <b>koymaz</b> (EC-9).</item>
/// </list>
/// </summary>
public class ExceptionHandlerContractTests
{
    /// <summary>Program.cs ile birebir: ResourcesPath OLMADAN gerçek resx localizer (key'lerin varlığını da doğrular).</summary>
    private static IStringLocalizer<ErrorMessages> CreateLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<ErrorMessages>>();
    }

    private sealed record ProblemBody(JsonElement Root, string RawJson, int StatusCode, string? ContentType);

    private static async Task<ProblemBody> InvokeAsync(IExceptionHandler handler, Exception exception)
    {
        // Activity başlat → handler'lar traceId'yi Activity.Current?.TraceId'den okur.
        using var activity = new Activity("test-exception-handler").Start();

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        handled.Should().BeTrue("handler kendi exception tipini işlemeli (true dönmeli)");

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(json);
        return new ProblemBody(doc.RootElement.Clone(), json, context.Response.StatusCode, context.Response.ContentType);
    }

    private static void AssertContract(ProblemBody body, int expectedStatus, string expectedType, string expectedCode)
    {
        body.StatusCode.Should().Be(expectedStatus);
        body.ContentType.Should().StartWith("application/problem+json", "EC-4: tüm handler'lar problem+json döner");

        body.Root.GetProperty("type").GetString().Should().Be(expectedType);
        body.Root.GetProperty("status").GetInt32().Should().Be(expectedStatus);
        body.Root.GetProperty("code").GetString().Should().Be(expectedCode, "EC-3: kararlı makine kodu");
        body.Root.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace("title lokalize çözülmeli");

        body.Root.TryGetProperty("traceId", out var traceId).Should().BeTrue("her yanıt traceId taşımalı");
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GlobalHandler_AnyException_Returns500_WithCode_NoTechnicalLeak()
    {
        const string sentinel = "SUPER_SECRET_STACK_DETAIL_8f3a2";
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance, CreateLocalizer());

        var body = await InvokeAsync(handler, new InvalidOperationException(sentinel));

        AssertContract(body, 500, "https://saydin.app/errors/internal-error", ApiErrorCodes.InternalError);
        body.RawJson.Should().NotContain(sentinel, "teknik exception mesajı/stack gövdeye sızmamalı");
    }

    [Fact]
    public async Task ValidationHandler_Returns400_WithCodeAndField()
    {
        var handler = new ValidationExceptionHandler(NullLogger<ValidationExceptionHandler>.Instance, CreateLocalizer());

        var body = await InvokeAsync(handler, new ValidationException("buyDate, sellDate'den önce olmalı", field: "buyDate"));

        AssertContract(body, 400, "https://saydin.app/errors/validation", ApiErrorCodes.Validation);
        body.Root.GetProperty("field").GetString().Should().Be("buyDate");
        body.Root.GetProperty("detail").GetString().Should().Be("buyDate, sellDate'den önce olmalı");
    }

    [Fact]
    public async Task ValidationHandler_NonMatchingException_ReturnsFalse_DoesNotWrite()
    {
        var handler = new ValidationExceptionHandler(NullLogger<ValidationExceptionHandler>.Instance, CreateLocalizer());
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("x"), CancellationToken.None);

        handled.Should().BeFalse("yalnız domain ValidationException handle edilmeli — diğerleri zincire bırakılır");
        context.Response.Body.Length.Should().Be(0, "eşleşmeyen exception'da gövde yazılmamalı");
    }

    [Fact]
    public async Task FeatureDisabledHandler_Returns403_WithCodeAndFeature()
    {
        var handler = new FeatureDisabledExceptionHandler(NullLogger<FeatureDisabledExceptionHandler>.Instance, CreateLocalizer());

        var body = await InvokeAsync(handler,
            new FeatureDisabledException("Bu özellik planınızda kapalı", featureKey: "extended_history"));

        AssertContract(body, 403, "https://saydin.app/errors/feature-disabled", ApiErrorCodes.FeatureDisabled);
        body.Root.GetProperty("feature").GetString().Should().Be("extended_history");
    }

    [Fact]
    public async Task PriceNotFoundHandler_Returns404_WithCodeAndNearestDates()
    {
        var handler = new PriceNotFoundExceptionHandler(NullLogger<PriceNotFoundExceptionHandler>.Instance, CreateLocalizer());

        var body = await InvokeAsync(handler,
            new PriceNotFoundException("USDTRY", new DateOnly(2020, 1, 1), [new DateOnly(2020, 1, 2)]));

        AssertContract(body, 404, "https://saydin.app/errors/price-not-found", ApiErrorCodes.PriceNotFound);
        body.Root.GetProperty("nearestDates").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task AssetNotFoundHandler_Returns404_WithCode()
    {
        var handler = new AssetNotFoundExceptionHandler(NullLogger<AssetNotFoundExceptionHandler>.Instance, CreateLocalizer());

        var body = await InvokeAsync(handler, new AssetNotFoundException("ZZZ"));

        AssertContract(body, 404, "https://saydin.app/errors/asset-not-found", ApiErrorCodes.AssetNotFound);
    }

    [Fact]
    public async Task ScenarioNotFoundHandler_Returns404_WithCode()
    {
        var handler = new ScenarioNotFoundExceptionHandler(NullLogger<ScenarioNotFoundExceptionHandler>.Instance, CreateLocalizer());

        var body = await InvokeAsync(handler, new ScenarioNotFoundException(Guid.NewGuid()));

        AssertContract(body, 404, "https://saydin.app/errors/scenario-not-found", ApiErrorCodes.ScenarioNotFound);
    }

    [Fact]
    public async Task ScenarioLimitHandler_Returns422_WithCodeAndLimit()
    {
        var handler = new ScenarioLimitExceededExceptionHandler(
            NullLogger<ScenarioLimitExceededExceptionHandler>.Instance, CreateLocalizer());

        var body = await InvokeAsync(handler, new ScenarioLimitExceededException(10));

        AssertContract(body, 422, "https://saydin.app/errors/scenario-limit-exceeded", ApiErrorCodes.ScenarioLimitExceeded);
        body.Root.GetProperty("limit").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task DailyLimitHandler_Returns429_WithCodeLimitAndResetAt()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 30, 10, 0, 0, TimeSpan.Zero));
        var handler = new DailyLimitExceededExceptionHandler(
            NullLogger<DailyLimitExceededExceptionHandler>.Instance, CreateLocalizer(), time);

        var body = await InvokeAsync(handler, new DailyLimitExceededException(20));

        AssertContract(body, 429, "https://saydin.app/errors/daily-limit-exceeded", ApiErrorCodes.DailyLimitExceeded);
        body.Root.GetProperty("limit").GetInt32().Should().Be(20);
        // resetAt = ertesi gün UTC gece yarısı, offset'li ISO-8601 ("O").
        body.Root.GetProperty("resetAt").GetString().Should().Be("2026-05-31T00:00:00.0000000+00:00");
    }

    [Fact]
    public async Task ExternalApiHandler_Returns502_WithCode_AndDoesNotLeakUpstreamSource()
    {
        var handler = new ExternalApiExceptionHandler(
            NullLogger<ExternalApiExceptionHandler>.Instance, CreateLocalizer());

        var body = await InvokeAsync(handler, new ExternalApiException("twelvedata", "upstream 500"));

        AssertContract(body, 502, "https://saydin.app/errors/external-api", ApiErrorCodes.ExternalApi);
        body.Root.TryGetProperty("source", out _).Should().BeFalse("EC-9: upstream source gövdeye sızmamalı");
        body.RawJson.Should().NotContain("twelvedata", "EC-9: iç kaynak adı istemciye dönmemeli");
    }
}
