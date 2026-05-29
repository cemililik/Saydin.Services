using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Constants;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public sealed class SavedScenarioRepository(SaydinDbContext context) : ISavedScenarioRepository
{
    public async Task<User?> GetUserByDeviceIdAsync(string deviceId, CancellationToken ct)
        => await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.DeviceId == deviceId, ct);

    public async Task<User> GetOrCreateUserAsync(string deviceId, CancellationToken ct)
    {
        // F2.3-8 ([G-C-02]): Atomik upsert + re-select. ON CONFLICT DO NOTHING
        // ile aynı device_id için iki paralel request'in yarış kazananı netleşir;
        // unique constraint ihlali bu noktada 500'e dönmez.
        //
        // Premium kullanıcı yarıştan korkmadan reset olmaz: ON CONFLICT DO NOTHING
        // yalnızca yeni satır yazımı iptal eder, var olan satırı değiştirmez.
        var newId = Guid.CreateVersion7();
        var now   = DateTimeOffset.UtcNow;

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO users (id, device_id, tier, created_at, last_seen_at)
            VALUES ({newId}, {deviceId}, {UserTiers.Free}, {now}, {now})
            ON CONFLICT (device_id) WHERE device_id IS NOT NULL DO NOTHING
            """,
            ct);

        // Re-select: ya yeni yazılan satırı (newId == returned.Id) ya da yarış
        // kazananını dönerek caller'ı garanti ile bir User ile besler.
        return await context.Users
            .AsNoTracking()
            .FirstAsync(u => u.DeviceId == deviceId, ct);
    }

    public async Task UpdateUserLastSeenAsync(User user, CancellationToken ct)
    {
        // F2.2-12 ([C-B-SavedScenario-3]): Throttled UPDATE. Repository katmanı sadece
        // SQL'i atar; "ne sıklıkta" kararı SavedScenarioService tarafında verilir
        // (in-memory throttle map). Buradan global NoTracking nedeniyle tracking-
        // free UPDATE atılır.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE users SET last_seen_at = {DateTimeOffset.UtcNow} WHERE id = {user.Id}
            """,
            ct);
    }

    public async Task<Asset?> GetActiveAssetBySymbolAsync(string symbol, CancellationToken ct)
        => await context.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Symbol == symbol && a.IsActive, ct);

    public async Task<IReadOnlyList<SavedScenario>> GetByUserIdAsync(Guid userId, CancellationToken ct)
        => await context.SavedScenarios
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

    public async Task<SavedScenario> CreateAsync(SavedScenario scenario, CancellationToken ct)
    {
        // Tracking sadece insert/update için gerekli; Add() çağrısı NoTracking
        // konfigürasyonu altında bile entity'yi Added state'ine alır.
        context.SavedScenarios.Add(scenario);
        await context.SaveChangesAsync(ct);
        return scenario;
    }

    public async Task<SavedScenario?> GetByIdAndUserIdAsync(Guid id, Guid userId, CancellationToken ct)
        => await context.SavedScenarios
            // Delete path'inde caller bu entity'yi Remove() yapıyor → tracking şart.
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

    public async Task DeleteAsync(SavedScenario scenario, CancellationToken ct)
    {
        context.SavedScenarios.Remove(scenario);
        await context.SaveChangesAsync(ct);
    }

    public async Task<int> CountByUserIdAsync(Guid userId, CancellationToken ct)
        => await context.SavedScenarios.AsNoTracking().CountAsync(s => s.UserId == userId, ct);
}
