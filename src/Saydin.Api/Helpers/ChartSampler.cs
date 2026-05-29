namespace Saydin.Api.Helpers;

/// <summary>
/// Zaman serisi noktalarını grafik gösterimi için sabit bir üst sınıra (varsayılan 60)
/// doğrusal aralıkla seyrekleştirir (down-sampling).
///
/// F3.1-6 ([C-D-Dca-5/6], [C-B-Dca-5]): Aynı seyrekleştirme indeks-matematiği
/// <c>WhatIfCalculator.SamplePriceHistory</c> ve <c>DcaCalculator.SampleChartData</c>
/// içinde birebir tekrar ediyordu. Tek, tip-bağımsız jenerik yardımcıya çıkarıldı —
/// her iki çağıran da kendi giriş/çıkış kayıt tiplerini bir projeksiyon delegesiyle verir.
/// </summary>
public static class ChartSampler
{
    /// <summary>Grafik için varsayılan maksimum nokta sayısı.</summary>
    public const int DefaultMaxPoints = 60;

    /// <summary>
    /// <paramref name="source"/> noktalarını en fazla <paramref name="maxPoints"/> adede indirger:
    /// <list type="bullet">
    ///   <item>Boşsa boş liste döner.</item>
    ///   <item>Nokta sayısı limitin altındaysa hepsi (sıra korunarak) projekte edilir.</item>
    ///   <item>Aşıyorsa baş ve son nokta dahil, eşit aralıklı <paramref name="maxPoints"/> indeks seçilir.</item>
    /// </list>
    /// Seçim deterministiktir (saf fonksiyon) ve <paramref name="selector"/> ile giriş tipi
    /// (<typeparamref name="TIn"/>) çıkış tipine (<typeparamref name="TOut"/>) eşlenir.
    /// </summary>
    public static IReadOnlyList<TOut> Downsample<TIn, TOut>(
        IReadOnlyList<TIn> source,
        Func<TIn, TOut> selector,
        int maxPoints = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);

        if (source.Count == 0)
            return Array.Empty<TOut>();

        if (source.Count <= maxPoints)
        {
            var all = new List<TOut>(source.Count);
            foreach (var item in source)
                all.Add(selector(item));
            return all;
        }

        // maxPoints == 1 (ve buraya geldiysek source.Count > 1): aşağıdaki indeks formülünde
        // (maxPoints - 1) sıfır olur → 0.0/0.0 = double.NaN → (int)NaN = int.MinValue →
        // source[int.MinValue] ArgumentOutOfRangeException ile çöker. Tek noktayı (ilk eleman)
        // döndürerek bu sıfıra bölmeyi kısa devre yaparız.
        if (maxPoints == 1)
            return new List<TOut> { selector(source[0]) };

        var result = new List<TOut>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
        {
            var idx = Math.Min(
                (int)((double)i * (source.Count - 1) / (maxPoints - 1)),
                source.Count - 1);
            result.Add(selector(source[idx]));
        }
        return result;
    }
}
