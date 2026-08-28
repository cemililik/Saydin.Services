using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public interface IPriceRepository
{
    // Every price-bearing method is a product read boundary: migration 020 legacy
    // all-null rows and incomplete/non-final authority tuples are never returned.
    Task<IReadOnlyList<Asset>> GetAllActiveAssetsAsync(CancellationToken ct);
    Task<AssetReadIdentity?> GetActiveAssetIdentityAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<AssetReadIdentity>> GetAllActiveAssetIdentitiesAsync(CancellationToken ct);
    Task<int> GetActiveAssetCountAsync(CancellationToken ct);
    Task<AssetCatalogVersion> GetAssetCatalogVersionAsync(CancellationToken ct);
    Task<IReadOnlyList<(Asset Asset, DateOnly? FirstDate, DateOnly? LastDate)>>
        GetAllActiveAssetsWithDateRangesAsync(CancellationToken ct);
    Task<PricePoint?> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct);

    /// <summary>
    /// İstenen tarihe en yakın işlem gününün fiyatını döner.
    /// Önce geriye doğru (≤ date, maxDays içinde) arar; bulamazsa ileriye doğru arar.
    /// Haftasonu / resmi tatil boşlukları için kullanılır.
    /// </summary>
    Task<PricePoint?> GetNearestPriceAsync(string symbol, DateOnly date, int maxDays, CancellationToken ct);

    /// <summary>
    /// Her istek tarihi için aynı backward-first nearest semantiğini tek, bounded
    /// veritabanı komutunda uygular. Sonuçlar istek sırasını ve duplicate tarihleri korur.
    /// </summary>
    Task<IReadOnlyList<PricePoint?>> GetNearestPricesAsync(
        string symbol,
        IReadOnlyList<DateOnly> dates,
        int maxDays,
        CancellationToken ct);
    Task<DateOnly?> GetLatestPriceDateAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct);
}

public sealed record AssetReadIdentity(Guid Id, string Symbol, string Source);

public sealed class AssetCatalogVersion
{
    public long Revision { get; init; }
    public byte[] CatalogSha256 { get; init; } = [];

    public bool IsValid => Revision > 0 && CatalogSha256.Length == 32;

    public string Token => IsValid
        ? $"r{Revision}-{Convert.ToHexString(CatalogSha256).ToLowerInvariant()}"
        : throw new InvalidOperationException("Asset catalog version is invalid.");
}
