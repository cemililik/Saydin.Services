using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface ISavedScenarioService
{
    // F2.2-3: deviceId artık IDeviceContext üzerinden (scoped) okunur.
    Task<IReadOnlyList<ScenarioResponse>> GetScenariosAsync(CancellationToken ct);
    Task<ScenarioResponse> SaveScenarioAsync(SaveScenarioRequest request, CancellationToken ct);
    Task DeleteScenarioAsync(Guid scenarioId, CancellationToken ct);
}
