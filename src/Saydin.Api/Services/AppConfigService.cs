using Microsoft.Extensions.Options;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;

namespace Saydin.Api.Services;

public interface IAppConfigService
{
    // The authenticated installation principal is resolved by the endpoint filter.
    Task<AppConfigResponse> GetConfigAsync(CancellationToken ct);
}

public sealed class AppConfigService(
    ISavedScenarioRepository scenarioRepository,
    IInstallationPrincipalContext principalContext,
    IOptions<PlanOptions> options) : IAppConfigService
{
    public async Task<AppConfigResponse> GetConfigAsync(CancellationToken ct)
    {
        var user = await scenarioRepository.GetUserByIdAsync(principalContext.PrincipalId, ct)
            ?? throw new InvalidOperationException("Authenticated installation principal is missing.");
        var tier = user.Tier;
        var tierOptions = options.Value.GetTierOptions(tier);

        return new AppConfigResponse(
            Tier:                   tier,
            DailyCalculationLimit:  tierOptions.DailyCalculationLimit,
            // Plan değerindeki 0 artık "sınırsız storage" değildir. API'nin
            // sistem hard cap'i istemciye effective limit olarak açıklanır.
            MaxSavedScenarios:      ScenarioLimits.GetEffectiveSaveLimit(tierOptions.MaxSavedScenarios),
            Features: new AppFeatureFlags(
                Comparison:          tierOptions.Features.Comparison,
                InflationAdjustment: tierOptions.Features.InflationAdjustment,
                Share:               tierOptions.Features.Share,
                Dca:                 tierOptions.Features.Dca,
                PriceHistoryMonths:  tierOptions.Features.PriceHistoryMonths));
    }
}
