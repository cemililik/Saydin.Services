using System.Collections.Immutable;

namespace Saydin.Shared.Constants;

/// <summary>
/// Fiyat serisi granülaritesi (`GetPriceRangeAsync` <c>interval</c> parametresi) için
/// kanonik değerler. Şu an yalnızca <see cref="Daily"/> üretimde desteklenir; günlük
/// fiyat noktaları DB'de tutulur. <see cref="Weekly"/> / <see cref="Monthly"/> ileride
/// down-sampling/aggregate olarak eklenebilir — değerler şimdiden ayrılmıştır ki cache
/// key fragmentasyonu (`prices:{symbol}:{from}:{to}:{interval}`) tutarlı kalsın.
///
/// F3.1-2 ([C-Tema-2]): Önceden "daily" string literal'i AssetService, WhatIfCalculator
/// ve AssetsEndpoints'te dağınık tekrar ediyordu — tek source-of-truth.
///
/// NOT: Bu, DCA <c>Period</c> (weekly/monthly = <b>alım sıklığı</b>) ile karıştırılmamalı.
/// PriceIntervals fiyat <b>verisinin granülaritesi</b>dir; DCA alımları her zaman günlük
/// fiyat verisi üzerinden hesaplanır.
/// </summary>
public static class PriceIntervals
{
    public const string Daily   = "daily";
    public const string Weekly  = "weekly";
    public const string Monthly = "monthly";

    /// <summary>Üretimde fiilen desteklenen interval değerleri (şu an yalnız <see cref="Daily"/>).</summary>
    public static readonly ImmutableArray<string> Supported = ImmutableArray.Create(Daily);

    /// <summary>Tanımlı tüm interval değerleri (gelecek genişlemeler dahil).</summary>
    public static readonly ImmutableArray<string> All = ImmutableArray.Create(
        Daily,
        Monthly,
        Weekly);

    /// <summary>O(1) "desteklenen mi?" kontrolü. Değerler lowercase saklanır; caller normalize eder.</summary>
    public static readonly IReadOnlySet<string> SupportedLookup =
        new HashSet<string>(Supported, StringComparer.Ordinal);
}
