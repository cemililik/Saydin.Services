using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public interface IPriceRepository
{
    Task<IReadOnlyList<Asset>> GetAllActiveAssetsAsync(CancellationToken ct);
    Task<int> GetActiveAssetCountAsync(CancellationToken ct);
    Task<IReadOnlyList<(Asset Asset, DateOnly? FirstDate, DateOnly? LastDate)>>
        GetAllActiveAssetsWithDateRangesAsync(CancellationToken ct);
    Task<PricePoint?> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct);

    /// <summary>
    /// İstenen tarihe en yakın işlem gününün fiyatını döner.
    /// Önce geriye doğru (≤ date, maxDays içinde) arar; bulamazsa ileriye doğru arar.
    /// Haftasonu / resmi tatil boşlukları için kullanılır.
    /// </summary>
    Task<PricePoint?> GetNearestPriceAsync(string symbol, DateOnly date, int maxDays, CancellationToken ct);
    Task<DateOnly?> GetLatestPriceDateAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct);
}
