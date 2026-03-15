using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

public interface IPriceIngestionRepository
{
    /// <summary>Belirtilen kaynağa ait aktif asset'leri döner.</summary>
    Task<IReadOnlyList<Asset>> GetActiveAssetsBySourceAsync(string source, CancellationToken ct);

    /// <summary>
    /// Fiyat noktalarını UPSERT yapar.
    /// Aynı (asset_id, price_date) için veri zaten varsa günceller.
    /// </summary>
    Task UpsertPricePointsAsync(IReadOnlyList<PricePoint> pricePoints, CancellationToken ct);

    /// <summary>Asset için veritabanındaki en son fiyat tarihini döner. Veri yoksa null.</summary>
    Task<DateOnly?> GetLatestPriceDateAsync(Guid assetId, CancellationToken ct);
}
