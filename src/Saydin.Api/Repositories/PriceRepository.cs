using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public sealed class PriceRepository(SaydinDbContext context) : IPriceRepository
{
    public async Task<IReadOnlyList<Asset>> GetAllActiveAssetsAsync(CancellationToken ct)
        => await context.Assets
            .Where(a => a.IsActive)
            .OrderBy(a => a.Category)
            .ThenBy(a => a.Symbol)
            .ToListAsync(ct);

    public async Task<PricePoint?> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct)
        => await context.PricePoints
            .Where(pp => pp.Asset.Symbol == symbol && pp.PriceDate == date)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct)
        => await context.PricePoints
            .Where(pp => pp.Asset.Symbol == symbol && pp.PriceDate >= from && pp.PriceDate <= to)
            .OrderBy(pp => pp.PriceDate)
            .ToListAsync(ct);
}
