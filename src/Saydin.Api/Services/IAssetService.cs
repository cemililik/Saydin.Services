using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

public interface IAssetService
{
    Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct);
    Task<PricePoint> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct);
    Task<DateOnly> GetLatestPriceDateAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, string interval, CancellationToken ct);
}
