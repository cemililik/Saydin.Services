using Microsoft.Extensions.Options;
using Saydin.Api.Helpers;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class AppConfigEndpoints
{
    public static IEndpointRouteBuilder MapAppConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/config").WithTags("Config");

        group.MapGet("/", GetConfigAsync)
            .WithName("GetAppConfig")
            .WithSummary("Kullanıcının plan konfigürasyonunu döner")
            .RequireDeviceId();

        return app;
    }

    private static async Task<IResult> GetConfigAsync(
        HttpContext httpContext,
        ISavedScenarioRepository repository,
        IOptions<PlanOptions> options,
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext).WithAction("config_fetch");
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");

        var user        = await repository.GetUserByDeviceIdAsync(deviceId, ct);
        var tier        = user?.Tier ?? "free";
        var tierOptions = options.Value.GetTierOptions(tier);

        log.WithData(new { tier })
           .Send(activityLogger);

        return Results.Ok(new AppConfigResponse(
            Tier:                   tier,
            DailyCalculationLimit:  tierOptions.DailyCalculationLimit,
            MaxSavedScenarios:      tierOptions.MaxSavedScenarios,
            Features: new AppFeatureFlags(
                Comparison:          tierOptions.Features.Comparison,
                InflationAdjustment: tierOptions.Features.InflationAdjustment,
                Share:               tierOptions.Features.Share,
                Dca:                 tierOptions.Features.Dca,
                PriceHistoryMonths:  tierOptions.Features.PriceHistoryMonths)));
    }
}
