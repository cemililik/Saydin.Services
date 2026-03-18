using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

public interface IInflationIngestionRepository
{
    Task<DateOnly?> GetLatestInflationDateAsync(CancellationToken ct);
    Task UpsertInflationRatesAsync(IReadOnlyList<InflationRate> rates, CancellationToken ct);
}
