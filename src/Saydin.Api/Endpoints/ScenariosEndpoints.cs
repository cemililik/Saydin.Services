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

        // F2.1-6 ([C-A-19/24]): MapGet("/") /v1/scenarios/ olarak match eder ve
        // trailing-slash olmadan gelen /v1/scenarios isteklerini 404'a düşürür.
        // Boş template ile yalnız grup ön ekine kayıt yapıldığında her iki form da çalışır.
        group.MapGet("", GetScenariosAsync)
            .WithName("GetScenarios")
            .WithSummary("Kullanıcının kaydettiği senaryoları listeler")
            .Produces<IReadOnlyList<Models.Responses.ScenarioResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireDeviceId();

        // APIR-002: 422 (ScenarioLimitExceeded) ve 409 (Conflict) açıkça beyan
        // edilir → Flutter codegen 422'yi typed handle edebilir.
        group.MapPost("", SaveScenarioAsync)
            .WithName("SaveScenario")
            .WithSummary("Yeni bir senaryo kaydeder")
            .Produces<Models.Responses.ScenarioResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireDeviceId();

        group.MapDelete("/{id:guid}", DeleteScenarioAsync)
            .WithName("DeleteScenario")
            .WithSummary("Kaydedilmiş bir senaryoyu siler")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireDeviceId();

        return app;
    }

    private static async Task<IResult> GetScenariosAsync(
        HttpContext httpContext,
        ISavedScenarioService service,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("scenario_list");

        var scenarios = await service.GetScenariosAsync(ct);

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

        var scenario = await service.SaveScenarioAsync(request, ct);

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

        await service.DeleteScenarioAsync(id, ct);

        log.WithData(new { scenarioId = id });

        return Results.NoContent();
    }
}
