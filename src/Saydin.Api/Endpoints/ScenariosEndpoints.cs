using Saydin.Api.Models.Requests;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class ScenariosEndpoints
{
    private const string DeviceIdHeader = "X-Device-ID";

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
        var deviceId = httpContext.Request.Headers[DeviceIdHeader].ToString();
        var scenarios = await service.GetScenariosAsync(deviceId, ct);
        return Results.Ok(scenarios);
    }

    private static async Task<IResult> SaveScenarioAsync(
        HttpContext httpContext,
        SaveScenarioRequest request,
        ISavedScenarioService service,
        CancellationToken ct)
    {
        var deviceId = httpContext.Request.Headers[DeviceIdHeader].ToString();
        var scenario = await service.SaveScenarioAsync(deviceId, request, ct);
        return Results.Created($"/v1/scenarios/{scenario.Id}", scenario);
    }

    private static async Task<IResult> DeleteScenarioAsync(
        Guid id,
        HttpContext httpContext,
        ISavedScenarioService service,
        CancellationToken ct)
    {
        var deviceId = httpContext.Request.Headers[DeviceIdHeader].ToString();
        await service.DeleteScenarioAsync(deviceId, id, ct);
        return Results.NoContent();
    }
}

file static class RouteBuilderExtensions
{
    internal static RouteHandlerBuilder RequireDeviceId(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var deviceId = ctx.HttpContext.Request.Headers["X-Device-ID"].ToString();
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return Results.Problem(
                    title: "X-Device-ID gerekli",
                    detail: "Bu endpoint'e erişmek için X-Device-ID header'ı gönderilmelidir.",
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://saydin.app/errors/missing-device-id");
            }
            return await next(ctx);
        });
}
