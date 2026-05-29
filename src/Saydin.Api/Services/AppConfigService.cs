using Microsoft.Extensions.Options;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;

namespace Saydin.Api.Services;

public interface IAppConfigService
{
    // F2.2-3: deviceId artık IDeviceContext üzerinden (scoped) okunur.
    Task<AppConfigResponse> GetConfigAsync(CancellationToken ct);
}

public sealed class AppConfigService(
    ISavedScenarioRepository scenarioRepository,
    IDeviceContext deviceContext,
    IOptions<PlanOptions> options) : IAppConfigService
{
    public async Task<AppConfigResponse> GetConfigAsync(CancellationToken ct)
    {
        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceContext.DeviceId, ct);
        var tier = user?.Tier ?? "free";
        var tierOptions = options.Value.GetTierOptions(tier);

        return new AppConfigResponse(
            Tier:                   tier,
            DailyCalculationLimit:  tierOptions.DailyCalculationLimit,
            MaxSavedScenarios:      tierOptions.MaxSavedScenarios,
            Features: new AppFeatureFlags(
                Comparison:          tierOptions.Features.Comparison,
                InflationAdjustment: tierOptions.Features.InflationAdjustment,
                Share:               tierOptions.Features.Share,
                Dca:                 tierOptions.Features.Dca,
                PriceHistoryMonths:  tierOptions.Features.PriceHistoryMonths));
    }
}
