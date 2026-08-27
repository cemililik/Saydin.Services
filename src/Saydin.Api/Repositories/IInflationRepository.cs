namespace Saydin.Api.Repositories;

public interface IInflationRepository
{
    /// <summary>
    /// buyDate ve sellDate'e karşılık gelen en yakın TÜFE endeks değerlerini döner.
    /// Tam ay verisi yoksa (TÜİK yayın gecikmesi), period_date &lt;= ilgili ay koşuluyla
    /// en son mevcut değer kullanılır (last-known-value).
    /// Veri hiç yoksa null döner (enflasyon hesabı opsiyonel).
    /// </summary>
    Task<(InflationIndexObservation? Buy, InflationIndexObservation? Sell)>
        GetIndexValuesAsync(DateOnly buyDate, DateOnly sellDate, CancellationToken ct);

    /// <summary>
    /// İstenen takvim aylarının TÜFE endekslerini tek sorguda ve yalnız exact ay eşleşmesiyle
    /// döner. Anahtarlar ayın ilk gününe normalize edilir. Yalnız migration 020'nin
    /// complete final EVDS/TÜİK CPI authority satırları görünür; legacy seed satırları
    /// ürün hesabına katılmaz ve eksik ay sözlükte yer almaz.
    /// </summary>
    Task<IReadOnlyDictionary<DateOnly, InflationIndexObservation>> GetExactIndexValuesAsync(
        IReadOnlyCollection<DateOnly> months,
        CancellationToken ct);

    /// <summary>
    /// Terminal deflatör için hedef aydan ileri olmayan en son complete-final CPI
    /// gözlemini döner. Ara katkı aylarının exact-only sözleşmesini değiştirmez.
    /// </summary>
    Task<InflationIndexObservation?> GetLatestFinalIndexValueAsync(
        DateOnly terminalMonth,
        CancellationToken ct);
}
