using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Saydin.Api.Endpoints;
using Saydin.Api.Services;
using Saydin.Api;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Saydin.Api.Tests.Endpoints;

public sealed class OpenApiSemanticContractTests
{
    [Fact]
    public async Task GeneratedDocument_BusinessRoutesExposeExactStatusAndProblemSemantics()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenApi();
        builder.Services.AddSingleton(Substitute.For<IWhatIfCalculator>());
        builder.Services.AddSingleton(Substitute.For<IDcaCalculator>());
        builder.Services.AddSingleton(Substitute.For<IAssetService>());
        builder.Services.AddSingleton(Substitute.For<IDailyLimitGuard>());
        builder.Services.AddSingleton(Substitute.For<IPlanLimitResolver>());
        builder.Services.AddSingleton(Substitute.For<IInstallationPrincipalContext>());
        builder.Services.AddSingleton(Substitute.For<ISavedScenarioService>());
        builder.Services.AddSingleton(Substitute.For<IAppConfigService>());
        builder.Services.AddSingleton(Substitute.For<IStringLocalizer<ErrorMessages>>());
        await using var app = builder.Build();
        app.MapWhatIfEndpoints();
        app.MapDcaEndpoints();
        app.MapAssetsEndpoints();
        app.MapScenariosEndpoints();
        app.MapAppConfigEndpoints();
        await app.StartAsync();

        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var generated = await provider.GetOpenApiDocumentAsync(CancellationToken.None);
        using var writer = new StringWriter();
        var openApiWriter = new OpenApiJsonWriter(writer);
        generated.SerializeAsV3(openApiWriter);
        using var document = JsonDocument.Parse(writer.ToString());

        var expected = new Dictionary<(string Path, string Method), string[]>
        {
            [("/v1/what-if/calculate", "post")] = ["200", "400", "401", "403", "404", "429", "503"],
            [("/v1/what-if/compare", "post")] = ["200", "400", "401", "403", "404", "429", "503"],
            [("/v1/what-if/reverse", "post")] = ["200", "400", "401", "403", "404", "429", "503"],
            [("/v1/what-if/dca", "post")] = ["200", "400", "401", "403", "404", "429", "503"],
            [("/v1/assets", "get")] = ["200", "401", "429", "503"],
            [("/v1/assets/{symbol}/price/{date}", "get")] = ["200", "401", "404", "429", "503"],
            [("/v1/assets/{symbol}/price-range", "get")] = ["200", "400", "401", "404", "429", "503"],
            [("/v1/scenarios", "get")] = ["200", "400", "401", "429", "503"],
            [("/v1/scenarios/page", "get")] = ["200", "400", "401", "429", "503"],
            [("/v1/scenarios", "post")] = ["201", "400", "401", "404", "409", "413", "415", "422", "429", "503"],
            [("/v1/scenarios/{id}", "delete")] = ["204", "401", "404", "429", "503"],
            [("/v1/config", "get")] = ["200", "400", "401", "429", "503"],
        };

        var paths = document.RootElement.GetProperty("paths");
        foreach (var (operation, statuses) in expected)
        {
            paths.EnumerateObject().Select(path => path.Name)
                .Should().Contain(operation.Path);
            var responses = paths.GetProperty(operation.Path)
                .GetProperty(operation.Method)
                .GetProperty("responses");

            responses.EnumerateObject().Select(property => property.Name)
                .Should().BeEquivalentTo(statuses);

            foreach (var status in statuses.Where(status => status[0] is '4' or '5'))
            {
                responses.GetProperty(status).GetProperty("content")
                    .TryGetProperty("application/problem+json", out _)
                    .Should().BeTrue($"{operation.Method.ToUpperInvariant()} {operation.Path} {status} must be ProblemDetails");
            }
        }
    }
}
