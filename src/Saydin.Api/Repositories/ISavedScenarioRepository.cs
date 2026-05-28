using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public interface ISavedScenarioRepository
{
    Task<User?> GetUserByDeviceIdAsync(string deviceId, CancellationToken ct);

    /// <summary>
    /// Atomik get-or-create: DeviceId için var olan kaydı döner; yoksa yarat-veya-yarış
    /// kazananını döner (PostgreSQL <c>INSERT ... ON CONFLICT DO NOTHING</c> + re-select).
    /// F2.3-8 ([G-C-02]): Aynı anda iki request gelirse `uq_users_device_id` unique
    /// constraint ihlali yerine yarış kazananı dolaylı olarak okunur, ikinci istek 500
    /// yerine sessizce mevcut kullanıcıyı alır.
    /// </summary>
    Task<User> GetOrCreateUserAsync(string deviceId, CancellationToken ct);

    Task UpdateUserLastSeenAsync(User user, CancellationToken ct);
    Task<Asset?> GetActiveAssetBySymbolAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<SavedScenario>> GetByUserIdAsync(Guid userId, CancellationToken ct);
    Task<SavedScenario> CreateAsync(SavedScenario scenario, CancellationToken ct);
    Task<SavedScenario?> GetByIdAndUserIdAsync(Guid id, Guid userId, CancellationToken ct);
    Task DeleteAsync(SavedScenario scenario, CancellationToken ct);
    Task<int> CountByUserIdAsync(Guid userId, CancellationToken ct);
}
