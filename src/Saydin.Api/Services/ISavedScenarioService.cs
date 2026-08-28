using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface ISavedScenarioService
{
    // Ownership is derived only from the authenticated installation principal.
    Task<IReadOnlyList<ScenarioResponse>> GetScenariosAsync(CancellationToken ct);
    Task<ScenarioPageResponse> GetScenarioPageAsync(int? limit, string? cursor, CancellationToken ct);
    Task<ScenarioResponse> SaveScenarioAsync(SaveScenarioRequest request, CancellationToken ct);
    Task DeleteScenarioAsync(Guid scenarioId, CancellationToken ct);
}
