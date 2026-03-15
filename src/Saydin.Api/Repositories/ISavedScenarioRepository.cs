using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public interface ISavedScenarioRepository
{
    Task<User?> GetUserByDeviceIdAsync(string deviceId, CancellationToken ct);
    Task<User> CreateUserAsync(string deviceId, CancellationToken ct);
    Task UpdateUserLastSeenAsync(User user, CancellationToken ct);
    Task<Asset?> GetActiveAssetBySymbolAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<SavedScenario>> GetByUserIdAsync(Guid userId, CancellationToken ct);
    Task<SavedScenario> CreateAsync(SavedScenario scenario, CancellationToken ct);
    Task<SavedScenario?> GetByIdAndUserIdAsync(Guid id, Guid userId, CancellationToken ct);
    Task DeleteAsync(SavedScenario scenario, CancellationToken ct);
    Task<int> CountByUserIdAsync(Guid userId, CancellationToken ct);
}
