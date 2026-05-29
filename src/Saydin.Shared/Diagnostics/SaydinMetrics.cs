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

    /// <summary>
    /// F2.3-4 ([C-C-22]): Activity log batch yazımının başarısız satır sayısı.
    /// Tag: outcome="retry_exhausted|cancelled". Operasyon ekibi observability
    /// boşluğu için bu sayaca dayanır — sessizce drop edilen log sayısı bilinir.
    /// </summary>
    public static readonly Counter<long> ActivityLogWriteFailures =
        Meter.CreateCounter<long>(
            "saydin.activity_log.write.failures.total",
            description: "Activity log batch yazımı başarısızlıkları (outcome tag'i ile)");

    /// <summary>
    /// F2.2-15 / F2.2-24 ([C-B-Channel-1], [G-B-06]): Channel DropWrite mode'da
    /// kuyruk dolu olduğunda düşürülen log sayısı. Operasyon ekibi spike alarm'ı
    /// için bu sayaca abone olur.
    /// </summary>
    public static readonly Counter<long> ActivityLogQueueDrops =
        Meter.CreateCounter<long>(
            "saydin.activity_log.queue.drops.total",
            description: "Channel kuyruğu dolduğundan dolayı düşürülen activity log sayısı");

    /// <summary>
    /// LOGR-028 follow-up: <c>ActivityLogBuilder</c> pre-validation aşamasında
    /// `data` JSONB byte size limitini aşan kayıt sayısı. Tag: action="...".
    /// Builder placeholder yazar, gerçek payload kullanıcı tarafına geri dönmez —
    /// bu sayaç olmadan trunc edilmiş data sessizce kaybolurdu.
    /// </summary>
    public static readonly Counter<long> ActivityLogDataTruncations =
        Meter.CreateCounter<long>(
            "saydin.activity_log.data.truncations.total",
            description: "Pre-validation aşamasında byte limit aşıldığı için truncate edilen data sayısı");
}
