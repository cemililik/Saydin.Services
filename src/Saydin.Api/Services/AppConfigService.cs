using Microsoft.Extensions.Options;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;

namespace Saydin.Api.Services;

public interface IAppConfigService
{
    Task<AppConfigResponse> GetConfigAsync(string deviceId, CancellationToken ct);
}

public sealed class AppConfigService(
    ISavedScenarioRepository scenarioRepository,
    IOptions<PlanOptions> options) : IAppConfigService
{
    public async Task<AppConfigResponse> GetConfigAsync(string deviceId, CancellationToken ct)
    {
        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
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
