namespace Saydin.Api.Repositories;

public interface IInflationRepository
{
    /// <summary>
    /// buyDate ve sellDate'e karşılık gelen en yakın TÜFE endeks değerlerini döner.
    /// Tam ay verisi yoksa (TÜİK yayın gecikmesi), period_date &lt;= ilgili ay koşuluyla
    /// en son mevcut değer kullanılır (last-known-value).
    /// Veri hiç yoksa null döner (enflasyon hesabı opsiyonel).
    /// </summary>
    Task<(decimal? BuyIndex, DateOnly? BuyIndexDate, decimal? SellIndex, DateOnly? SellIndexDate)>
        GetIndexValuesAsync(DateOnly buyDate, DateOnly sellDate, CancellationToken ct);
}
