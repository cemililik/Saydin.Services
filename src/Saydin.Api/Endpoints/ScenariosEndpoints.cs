using Saydin.Api.Middleware;
using Saydin.Api.Models.Requests;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class ScenariosEndpoints
{
    public static IEndpointRouteBuilder MapScenariosEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/scenarios")
            .WithTags("Scenarios");

        group.MapGet("/", GetScenariosAsync)
            .WithName("GetScenarios")
            .WithSummary("Kullanıcının kaydettiği senaryoları listeler")
            .RequireDeviceId();

        group.MapPost("/", SaveScenarioAsync)
            .WithName("SaveScenario")
            .WithSummary("Yeni bir senaryo kaydeder")
            .RequireDeviceId();

        group.MapDelete("/{id:guid}", DeleteScenarioAsync)
            .WithName("DeleteScenario")
            .WithSummary("Kaydedilmiş bir senaryoyu siler")
            .RequireDeviceId();

        return app;
    }

    private static async Task<IResult> GetScenariosAsync(
        HttpContext httpContext,
        ISavedScenarioService service,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("scenario_list");

        var scenarios = await service.GetScenariosAsync(httpContext.GetRequiredDeviceId(), ct);

        log.WithData(new { scenarioCount = scenarios.Count });

        return Results.Ok(scenarios);
    }

    private static async Task<IResult> SaveScenarioAsync(
        HttpContext httpContext,
        SaveScenarioRequest request,
        ISavedScenarioService service,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("scenario_save");

        var scenario = await service.SaveScenarioAsync(httpContext.GetRequiredDeviceId(), request, ct);

        log.WithData(new
        {
            scenarioId = scenario.Id,
            type = request.Type,
            assetSymbol = request.AssetSymbol,
            label = request.Label
        });

        return Results.Created($"/v1/scenarios/{scenario.Id}", scenario);
    }

    private static async Task<IResult> DeleteScenarioAsync(
        Guid id,
        HttpContext httpContext,
        ISavedScenarioService service,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("scenario_delete");

        await service.DeleteScenarioAsync(httpContext.GetRequiredDeviceId(), id, ct);

        log.WithData(new { scenarioId = id });

        return Results.NoContent();
    }
}
