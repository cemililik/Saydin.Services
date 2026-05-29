using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

public interface IInflationIngestionRepository
{
    /// <summary>
    /// INGR-012: Belirli bir <paramref name="source"/> için en son <c>period_date</c>.
    /// EVDS backfill anchor'ı bunu <c>tuik</c> ile çağırır — böylece seed (seed-approximation)
    /// satırları gerçek TÜİK backfill'ini engellemez (max-all anchor'ı tarihsel tuik'i atlıyordu).
    /// </summary>
    Task<DateOnly?> GetLatestInflationDateAsync(string source, CancellationToken ct);
    Task UpsertInflationRatesAsync(IReadOnlyList<InflationRate> rates, CancellationToken ct);
}
