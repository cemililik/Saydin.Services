namespace Saydin.Shared.Constants;

/// <summary>
/// F4-6 (KVKK veri minimizasyonu · F2.1-5 / [C-A-14]): activity_logs.data içine ham
/// finansal tutar (<c>request.Amount</c> / <c>TargetAmount</c> / <c>PeriodicAmount</c>)
/// yazmak yerine kaba bir aralık etiketi yazılır. Böylece "cihaz + IP + tam tutar"
/// kombinasyonuyla kullanıcı profillemesi engellenir; analytics değeri (popülerlik,
/// büyüklük dağılımı) korunur. Sonuç TL tutarları (ProfitLossTry, CurrentValueTry vb.)
/// activity_logs'a HİÇ yazılmaz; yalnızca yüzde alanları (ProfitLossPercent,
/// RealProfitLossPercent, IsProfit) tutulur — bunlar mutlak para figürü içermez.
///
/// Hash KULLANILMAZ: düşük entropili (birkaç bin makul yuvarlak değer) bir tutarın
/// hash'i brute-force ile geri çevrilebilir; gerçek bir KVKK önlemi değildir. Bucketing
/// dürüst minimizasyondur. Sınır değerleri tek source-of-truth buradadır;
/// <c>docs/decisions/ADR-006-activity-log-financial-policy.md</c> ve gizlilik politikası
/// buna atıfta bulunur.
/// </summary>
public static class AmountBucket
{
    /// <summary>
    /// Pozitif bir tutarı kaba büyüklük aralığı etiketine indirger (ör. 2.500 → "1k-10k").
    /// Aralık sınırları TL ölçeğine göredir; <c>amountType</c> "units"/"grams" olduğunda
    /// log'da etiketin yanına yazılan <c>amountType</c> ile birlikte yorumlanır
    /// (etiket o birim cinsinden bir aralıktır). Karşılaştırma yalnızca <see cref="decimal"/>
    /// ile yapılır (CLAUDE.md: finansal değerlerde double/float yasak).
    /// </summary>
    public static string Coarse(decimal amount) => amount switch
    {
        <= 0m        => "0",
        < 1_000m     => "0-1k",
        < 10_000m    => "1k-10k",
        < 100_000m   => "10k-100k",
        < 1_000_000m => "100k-1M",
        _            => "1M+",
    };
}
