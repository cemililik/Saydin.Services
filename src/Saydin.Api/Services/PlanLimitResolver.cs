using Microsoft.Extensions.Options;
using Saydin.Api.Options;
using Saydin.Api.Repositories;

namespace Saydin.Api.Services;

/// <summary>
/// F5 / SVCR-015 follow-up: endpoint katmanı CLAUDE.md "Endpoints → Services →
/// Repositories" kuralı gereği repository'ye doğrudan erişmemeli. Bu service
/// kullanıcının tier'ına göre `DailyAssetQueryLimit`'i (ve gelecekteki diğer
/// kota'ları) tek noktada çözer. Sonar S107 ile birlikte AssetsEndpoints
/// handler imzalarındaki 8 parametre 6'ya iner (`scenarioRepository` + `plans`
/// → tek `planLimits`).
/// </summary>
public interface IPlanLimitResolver
{
    /// <summary>Asset query endpoint'leri için günlük limit.</summary>
    Task<int> ResolveDailyAssetQueryLimitAsync(string deviceId, CancellationToken ct);
}

public sealed class PlanLimitResolver(
    ISavedScenarioRepository scenarioRepository,
    IOptions<PlanOptions> options) : IPlanLimitResolver
{
    public async Task<int> ResolveDailyAssetQueryLimitAsync(string deviceId, CancellationToken ct)
    {
        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        return options.Value.GetTierOptions(user?.Tier).DailyAssetQueryLimit;
    }
}
