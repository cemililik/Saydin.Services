using System.Diagnostics.Metrics;

namespace Saydin.Shared.Diagnostics;

public static class SaydinMetrics
{
    /// <summary>
    /// OpenTelemetry MeterProvider'a kayıt için meter adı. Tüm metric source'larını
    /// hem Saydin.Api hem Saydin.PriceIngestion bu tek isim üzerinden yayınlar.
    /// Service ayrımı `service.name` resource attribute'u ile yapılır; meter adı
    /// Shared kütüphaneyi referans aldığı için `"Saydin"` olarak tutulur (review F1.5-1).
    /// </summary>
    public const string MeterName = "Saydin";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

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

    /// <summary>
    /// EVDS / TÜFE ingestion başarısızlık sayısı. Tag: source="evds", outcome="auth|http|other".
    /// Worker job kaydı yazmadığı için operasyon ekibi alarm'ı bu metriğe göre kurabilir
    /// (review H-7 / M-17).
    /// </summary>
    public static readonly Counter<long> InflationIngestionFailures =
        Meter.CreateCounter<long>(
            "saydin.inflation.ingestion.failures.total",
            description: "EVDS TÜFE ingestion başarısızlıkları (outcome tag'i ile)");
}
