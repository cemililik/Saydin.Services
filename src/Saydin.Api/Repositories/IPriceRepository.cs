using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public interface IPriceRepository
{
    Task<IReadOnlyList<Asset>> GetAllActiveAssetsAsync(CancellationToken ct);
    Task<PricePoint?> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct);
    Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct);
}
