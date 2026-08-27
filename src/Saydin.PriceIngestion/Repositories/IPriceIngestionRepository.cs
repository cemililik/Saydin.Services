using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

public interface IPriceIngestionRepository
{
    /// <summary>Belirtilen kaynağa ait aktif asset'leri döner.</summary>
    Task<IReadOnlyList<Asset>> GetActiveAssetsBySourceAsync(string source, CancellationToken ct);

    /// <summary>Provider completeness contractı için bilinen piyasa tatilleri.</summary>
    Task<IReadOnlySet<DateOnly>> GetMarketHolidaysAsync(
        Guid assetId, DateOnly from, DateOnly to, CancellationToken ct);
}
