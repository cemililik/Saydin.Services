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

    public Task<int> GetActiveAssetCountAsync(CancellationToken ct)
        => context.Assets.CountAsync(a => a.IsActive, ct);

    public async Task<PricePoint?> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct)
        => await context.PricePoints
            .Where(pp => pp.Asset.Symbol == symbol && pp.PriceDate == date)
            .FirstOrDefaultAsync(ct);

    public async Task<DateOnly?> GetLatestPriceDateAsync(string symbol, CancellationToken ct)
        => await context.PricePoints
            .Where(pp => pp.Asset.Symbol == symbol)
            .Select(pp => (DateOnly?)pp.PriceDate)
            .MaxAsync(ct);

    public async Task<IReadOnlyList<(Asset Asset, DateOnly? FirstDate, DateOnly? LastDate)>>
        GetAllActiveAssetsWithDateRangesAsync(CancellationToken ct)
    {
        var assets = await context.Assets
            .Where(a => a.IsActive)
            .OrderBy(a => a.Category)
            .ThenBy(a => a.Symbol)
            .ToListAsync(ct);

        if (assets.Count == 0)
            return Array.Empty<(Asset, DateOnly?, DateOnly?)>();

        var assetIds = assets.Select(a => a.Id).ToHashSet();

        var ranges = await context.PricePoints
            .Where(pp => assetIds.Contains(pp.AssetId))
            .GroupBy(pp => pp.AssetId)
            .Select(g => new
            {
                AssetId   = g.Key,
                FirstDate = g.Min(pp => (DateOnly?)pp.PriceDate),
                LastDate  = g.Max(pp => (DateOnly?)pp.PriceDate),
            })
            .ToDictionaryAsync(r => r.AssetId, ct);

        return assets
            .Select(a =>
            {
                ranges.TryGetValue(a.Id, out var r);
                return (a, r?.FirstDate, r?.LastDate);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct)
        => await context.PricePoints
            .Where(pp => pp.Asset.Symbol == symbol && pp.PriceDate >= from && pp.PriceDate <= to)
            .OrderBy(pp => pp.PriceDate)
            .ToListAsync(ct);
}
