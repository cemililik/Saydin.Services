using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface ISavedScenarioService
{
    Task<IReadOnlyList<ScenarioResponse>> GetScenariosAsync(string deviceId, CancellationToken ct);
    Task<ScenarioResponse> SaveScenarioAsync(string deviceId, SaveScenarioRequest request, CancellationToken ct);
    Task DeleteScenarioAsync(string deviceId, Guid scenarioId, CancellationToken ct);
}
