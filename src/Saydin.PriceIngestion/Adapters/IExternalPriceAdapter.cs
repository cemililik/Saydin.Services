using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

/// <summary>
/// Dış finansal API adaptörlerinin ortak sözleşmesi.
/// Her adaptör bu interface'i implement eder: TCMB, CoinGecko, GoldAPI, TwelveData.
/// </summary>
public interface IExternalPriceAdapter
{
    /// <summary>Adaptörün veri kaynağını tanımlayan ad (örn: "tcmb", "coingecko")</summary>
    string Source { get; }

    /// <summary>
    /// Belirtilen tarih aralığı için fiyat verisi çeker.
    /// Polly retry ve circuit breaker implementasyonu her adapter'da zorunludur.
    /// </summary>
    /// <param name="assetId">Veritabanındaki asset UUID'si — PricePoint'lere doğrudan atanır.</param>
    Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        Guid assetId,
        string assetSymbol,
        string sourceId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct);
}
