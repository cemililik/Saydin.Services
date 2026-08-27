using System.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Middleware;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Services;
using Saydin.Shared.Constants;

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
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential();

        group.MapGet("/page", GetScenarioPageAsync)
            .WithName("GetScenarioPage")
            .WithSummary("Kullanıcının kaydettiği senaryoları cursor ile sayfalar")
            .Produces<ScenarioPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential();

        // ScenarioLimitExceeded is the only create-time conflict-like domain result
        // and is intentionally exposed as 422. No runtime path emits 409.
        group.MapPost("", SaveScenarioAsync)
            .WithName("SaveScenario")
            .WithSummary("Yeni bir senaryo kaydeder")
            .Produces<Models.Responses.ScenarioResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .Accepts<SaveScenarioRequest>("application/json")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential();

        group.MapDelete("/{id:guid}", DeleteScenarioAsync)
            .WithName("DeleteScenario")
            .WithSummary("Kaydedilmiş bir senaryoyu siler")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential();

        return app;
    }

    private static async Task<IResult> GetScenarioPageAsync(
        string? limit,
        string? cursor,
        HttpContext httpContext,
        ISavedScenarioService service,
        IStringLocalizer<ErrorMessages> localizer,
        CancellationToken ct)
    {
        // Keep the simple query parameters in the handler signature so native
        // OpenAPI/codegen can discover them. ParsePageQuery remains authoritative
        // because it also rejects duplicate values and applies invariant parsing.
        _ = limit;
        _ = cursor;
        var (parsedLimit, parsedCursor) = ParsePageQuery(httpContext.Request.Query, localizer);
        var log = httpContext.GetOrCreateActivityLog(ActivityActions.ScenarioList);
        var page = await service.GetScenarioPageAsync(parsedLimit, parsedCursor, ct);

        log.WithData(CreateListActivityData(
            page.Items.Count,
            paginated: true,
            hasNextPage: page.NextCursor is not null));
        return Results.Ok(page);
    }

    private static async Task<IResult> GetScenariosAsync(
        HttpContext httpContext,
        ISavedScenarioService service,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog(ActivityActions.ScenarioList);

        var scenarios = await service.GetScenariosAsync(ct);

        log.WithData(CreateListActivityData(scenarios.Count, paginated: false, hasNextPage: false));

        return Results.Ok(scenarios);
    }

    private static async Task<IResult> SaveScenarioAsync(
        HttpContext httpContext,
        ISavedScenarioService service,
        IOptions<JsonOptions> jsonOptions,
        IStringLocalizer<ErrorMessages> localizer,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog(ActivityActions.ScenarioSave);

        if (!httpContext.Request.HasJsonContentType())
        {
            return Results.Problem(
                detail: localizer["UnsupportedJsonContentTypeDetail"],
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: localizer["UnsupportedJsonContentType"],
                type: "https://saydin.app/errors/unsupported-media-type",
                extensions: new Dictionary<string, object?>
                {
                    ["traceId"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier,
                    ["code"] = Exceptions.ApiErrorCodes.UnsupportedMediaType,
                });
        }

        var request = await ScenarioRequestBodyReader.ReadAsync(
            httpContext.Request, jsonOptions.Value.SerializerOptions, localizer, ct);

        var scenario = await service.SaveScenarioAsync(request, ct);

        log.WithData(CreateSaveActivityData(request, scenario));

        return Results.Created($"/v1/scenarios/{scenario.Id}", scenario);
    }

    private static async Task<IResult> DeleteScenarioAsync(
        Guid id,
        HttpContext httpContext,
        ISavedScenarioService service,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog(ActivityActions.ScenarioDelete);

        await service.DeleteScenarioAsync(id, ct);

        log.WithData(new { scenarioId = id });

        return Results.NoContent();
    }

    /// <summary>
    /// Serbest metin label activity payload'ına taşınmaz. Analytics yalnız label'ın
    /// varlığını düşük kardinaliteli boolean olarak görür.
    /// </summary>
    internal static object CreateSaveActivityData(
        SaveScenarioRequest request,
        Models.Responses.ScenarioResponse scenario) => new
        {
            scenarioId = scenario.Id,
            type = request.Type,
            // The service response carries the repository-validated canonical symbol.
            // Never copy the untrusted request spelling into the audit payload.
            assetSymbol = scenario.AssetSymbol,
            hasLabel = !string.IsNullOrWhiteSpace(request.Label),
        };

    internal static (int? Limit, string? Cursor) ParsePageQuery(
        IQueryCollection query,
        IStringLocalizer<ErrorMessages> localizer)
    {
        int? limit = null;
        if (query.TryGetValue("limit", out var limitValues))
        {
            if (limitValues.Count != 1
                || !int.TryParse(
                    limitValues[0],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                throw new Saydin.Shared.Exceptions.ValidationException(
                    string.Format(localizer["ScenarioPageLimitInvalid"],
                        ScenarioLimits.MaxPageSize),
                    field: "limit");
            }
            limit = parsed;
        }

        string? cursor = null;
        if (query.TryGetValue("cursor", out var cursorValues))
        {
            if (cursorValues.Count != 1)
                throw new Saydin.Shared.Exceptions.ValidationException(
                    localizer["ScenarioCursorInvalid"], field: "cursor");
            cursor = string.IsNullOrWhiteSpace(cursorValues[0]) ? null : cursorValues[0];
        }

        return (limit, cursor);
    }

    internal static object CreateListActivityData(
        int scenarioCount,
        bool paginated,
        bool hasNextPage) => new
        {
            scenarioCount,
            paginated,
            hasNextPage,
        };
}
