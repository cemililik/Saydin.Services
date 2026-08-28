using Saydin.Api.Middleware;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class AppConfigEndpoints
{
    public static IEndpointRouteBuilder MapAppConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/config").WithTags("Config");

        // F2.1-6: trailing-slash bağımsız route.
        group.MapGet("", GetConfigAsync)
            .WithName("GetAppConfig")
            .WithSummary("Kullanıcının plan konfigürasyonunu döner")
            .Produces<Models.Responses.AppConfigResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential();

        return app;
    }

    private static async Task<IResult> GetConfigAsync(
        HttpContext httpContext,
        IAppConfigService configService,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("config_fetch");

        var config = await configService.GetConfigAsync(ct);

        log.WithData(new { tier = config.Tier });
        return Results.Ok(config);
    }
}
