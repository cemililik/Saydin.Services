using Saydin.Api.Helpers;
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

    private static string GetDeviceId(HttpContext httpContext) =>
        httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");

    private static async Task<IResult> GetScenariosAsync(
        HttpContext httpContext,
        ISavedScenarioService service,
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext).WithAction("scenario_list");

        var scenarios = await service.GetScenariosAsync(GetDeviceId(httpContext), ct);

        log.WithData(new { scenarioCount = scenarios.Count })
           .Send(activityLogger);

        return Results.Ok(scenarios);
    }

    private static async Task<IResult> SaveScenarioAsync(
        HttpContext httpContext,
        SaveScenarioRequest request,
        ISavedScenarioService service,
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext).WithAction("scenario_save");

        var scenario = await service.SaveScenarioAsync(GetDeviceId(httpContext), request, ct);

        log.WithData(new
        {
            scenarioId = scenario.Id,
            type = request.Type,
            assetSymbol = request.AssetSymbol,
            label = request.Label
        }).Send(activityLogger);

        return Results.Created($"/v1/scenarios/{scenario.Id}", scenario);
    }

    private static async Task<IResult> DeleteScenarioAsync(
        Guid id,
        HttpContext httpContext,
        ISavedScenarioService service,
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext).WithAction("scenario_delete");

        await service.DeleteScenarioAsync(GetDeviceId(httpContext), id, ct);

        log.WithData(new { scenarioId = id })
           .Send(activityLogger);

        return Results.NoContent();
    }
}
