using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public sealed class SavedScenarioRepository(SaydinDbContext context) : ISavedScenarioRepository
{
    public async Task<User?> GetUserByDeviceIdAsync(string deviceId, CancellationToken ct)
        => await context.Users
            .FirstOrDefaultAsync(u => u.DeviceId == deviceId, ct);

    public async Task<User> CreateUserAsync(string deviceId, CancellationToken ct)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            Tier = "free",
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateUserLastSeenAsync(User user, CancellationToken ct)
    {
        user.LastSeenAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(ct);
    }

    public async Task<Asset?> GetActiveAssetBySymbolAsync(string symbol, CancellationToken ct)
        => await context.Assets
            .FirstOrDefaultAsync(a => a.Symbol == symbol && a.IsActive, ct);

    public async Task<IReadOnlyList<SavedScenario>> GetByUserIdAsync(Guid userId, CancellationToken ct)
        => await context.SavedScenarios
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

    public async Task<SavedScenario> CreateAsync(SavedScenario scenario, CancellationToken ct)
    {
        context.SavedScenarios.Add(scenario);
        await context.SaveChangesAsync(ct);
        return scenario;
    }

    public async Task<SavedScenario?> GetByIdAndUserIdAsync(Guid id, Guid userId, CancellationToken ct)
        => await context.SavedScenarios
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

    public async Task DeleteAsync(SavedScenario scenario, CancellationToken ct)
    {
        context.SavedScenarios.Remove(scenario);
        await context.SaveChangesAsync(ct);
    }

    public async Task<int> CountByUserIdAsync(Guid userId, CancellationToken ct)
        => await context.SavedScenarios.CountAsync(s => s.UserId == userId, ct);
}
