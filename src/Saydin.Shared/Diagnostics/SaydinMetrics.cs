using System.Diagnostics.Metrics;

namespace Saydin.Shared.Diagnostics;

public static class SaydinMetrics
{
    private static readonly Meter Meter = new("Saydin.Api", "1.0.0");

    /// <summary>Toplam hesaplama sayısı (asset.symbol, user.tier tag'leri ile)</summary>
    public static readonly Counter<long> WhatIfCalculations =
        Meter.CreateCounter<long>(
            "saydin.whatif.calculations.total",
            description: "Toplam ya-alsaydım hesaplama sayısı");

    /// <summary>Hesaplama süresi (ms cinsinden histogram)</summary>
    public static readonly Histogram<double> CalculationDuration =
        Meter.CreateHistogram<double>(
            "saydin.whatif.calculation.duration.ms",
            unit: "ms",
            description: "Ya-alsaydım hesaplama süresi");

    /// <summary>Fiyat bulunamayan sorgu sayısı</summary>
    public static readonly Counter<long> PriceNotFoundCount =
        Meter.CreateCounter<long>(
            "saydin.price.not_found.total",
            description: "Fiyat bulunamayan sorgu sayısı");
}
