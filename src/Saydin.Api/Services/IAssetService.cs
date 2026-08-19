using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

public interface IAssetService
{
    Task<AssetCatalogVersion> GetCatalogVersionAsync(CancellationToken ct);
    Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct);
    Task<IReadOnlyList<AssetResponse>> GetAllAssetInfoAsync(CancellationToken ct);

    /// <summary>
    /// Aktif asset listesini sembol → Asset map'i olarak döner (cache'lidir).
    /// Calculator'lar tek sembol lookup için tüm listeyi tarayıp <c>FirstOrDefault</c>
    /// yapmak yerine bu metodu kullanır (O(1) lookup).
    /// </summary>
    Task<Asset?> GetBySymbolAsync(string symbol, CancellationToken ct);
    Task<PricePoint> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct);

    /// <summary>
    /// İstenen tarihe en yakın işlem gününün fiyatını döner (±7 gün penceresi).
    /// Haftasonu veya resmi tatile denk gelen tarihler için kullanılır.
    /// </summary>
    Task<PricePoint> GetNearestPriceAsync(string symbol, DateOnly date, CancellationToken ct);
    Task<IReadOnlyList<PricePoint>> GetNearestPricesAsync(
        string symbol,
        IReadOnlyList<DateOnly> dates,
        CancellationToken ct);
    Task<DateOnly> GetLatestPriceDateAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, string interval, CancellationToken ct);
}
