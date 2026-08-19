using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

/// <summary>
/// Enflasyon (TÜFE / CPI) endeks adaptörlerinin ortak sözleşmesi.
/// PricePoint döndüren <see cref="IExternalPriceAdapter"/>'dan ayrılır çünkü
/// enflasyon verisi aylık <see cref="InflationRate"/> kayıtları üretir, OHLCV değil.
/// </summary>
public interface IInflationAdapter
{
    /// <summary>Adaptörün veri kaynağını tanımlayan ad (örn: "tuik", "evds").</summary>
    string Source { get; }

    /// <summary>
    /// Belirtilen aralıkta aylık endeks değerlerini çeker.
    /// `from` ve `to` her ayın 1. günü olarak verilmelidir.
    /// Polly retry ve timeout zorunludur (geçici hatalar yukarı fırlatılır,
    /// kalıcı 4xx hataları HttpRequestException olarak iletilir).
    /// </summary>
    Task<AdapterOutcome<InflationRate>> FetchRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct);
}
