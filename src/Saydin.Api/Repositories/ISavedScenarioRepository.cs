using Saydin.Shared.Entities;
using Saydin.Api.Models;

namespace Saydin.Api.Repositories;

public interface ISavedScenarioRepository
{
    Task<User?> GetUserByIdAsync(Guid principalId, CancellationToken ct);
    Task UpdateUserLastSeenAsync(User user, CancellationToken ct);
    Task<Asset?> GetActiveAssetBySymbolAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<SavedScenario>> GetByUserIdAsync(Guid userId, int limit, CancellationToken ct);
    Task<IReadOnlyList<SavedScenario>> GetPageByUserIdAsync(
        Guid userId,
        ScenarioCursor? cursor,
        int take,
        CancellationToken ct);
    /// <summary>
    /// Serializes writers for one user, evaluates the effective plan limit and
    /// inserts in the same PostgreSQL transaction.
    /// </summary>
    Task<SavedScenario> CreateWithinLimitAsync(
        SavedScenario scenario,
        int effectiveLimit,
        CancellationToken ct);
    Task<SavedScenario?> GetByIdAndUserIdAsync(Guid id, Guid userId, CancellationToken ct);
    Task DeleteAsync(SavedScenario scenario, CancellationToken ct);
}
