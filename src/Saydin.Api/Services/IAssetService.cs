using Saydin.Api.Models.Responses;
using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

public interface IAssetService
{
    Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct);
    Task<IReadOnlyList<AssetResponse>> GetAllAssetInfoAsync(CancellationToken ct);
    Task<PricePoint> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct);

    /// <summary>
    /// İstenen tarihe en yakın işlem gününün fiyatını döner (±7 gün penceresi).
    /// Haftasonu veya resmi tatile denk gelen tarihler için kullanılır.
    /// </summary>
    Task<PricePoint> GetNearestPriceAsync(string symbol, DateOnly date, CancellationToken ct);
    Task<DateOnly> GetLatestPriceDateAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, string interval, CancellationToken ct);
}
