using System.Diagnostics;
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
    private static readonly long ProcessStartTimeUnixSeconds = GetProcessStartTimeUnixSeconds();

    private static long GetProcessStartTimeUnixSeconds()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Süreç restart'ını aynı Prometheus target label seti üzerinde gözlenebilir kılan
    /// sabit başlangıç zamanı. Gauge process boyunca değişmez; restart sonrası yeni
    /// değer yayınlanır ve alert <c>changes()</c> ile reset'i yakalar.
    /// </summary>
    public static readonly ObservableGauge<long> ProcessStartTime =
        Meter.CreateObservableGauge(
            "saydin.process.start_time.seconds",
            () => ProcessStartTimeUnixSeconds,
            unit: "s",
            description: "Process start timestamp as Unix seconds");

    /// <summary>Toplam WhatIf çağrısı (bounded operation/outcome tag'leri ile)</summary>
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

    /// <summary>Toplam DCA çağrısı (bounded operation/outcome tag'leri ile)</summary>
    public static readonly Counter<long> DcaCalculations =
        Meter.CreateCounter<long>(
            "saydin.dca.calculations.total",
            description: "Toplam DCA hesaplama sayısı");

    /// <summary>DCA hesaplama süresi (ms cinsinden histogram)</summary>
    public static readonly Histogram<double> DcaCalculationDuration =
        Meter.CreateHistogram<double>(
            "saydin.dca.calculation.duration.ms",
            unit: "ms",
            description: "DCA hesaplama süresi");

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
    /// Contract-v2 worker'ın provider çağrısından önce reddettiği eksik/stale calendar
    /// coverage sayısı. Tag'ler: source ve reason (bounded allowlist).
    /// </summary>
    public static readonly Counter<long> MarketCalendarNotReady =
        Meter.CreateCounter<long>(
            "saydin.ingestion.calendar.not_ready.total",
            description: "Authoritative market calendar readiness reddi");

    public static readonly Gauge<long> IngestionLastAttemptTimestamp =
        Meter.CreateGauge<long>(
            "saydin.ingestion.last_attempt.timestamp.seconds",
            description: "Son durable ingestion attempt başlangıcının Unix zamanı");

    public static readonly Gauge<long> IngestionLastSuccessTimestamp =
        Meter.CreateGauge<long>(
            "saydin.ingestion.last_success.timestamp.seconds",
            description: "Son authoritative terminal ingestion başarısının Unix zamanı");

    public static readonly Gauge<long> IngestionLag =
        Meter.CreateGauge<long>(
            "saydin.ingestion.lag.seconds",
            unit: "s",
            description: "Durable başarılı veri horizon'ının DB saatine göre gecikmesi");

    public static readonly Gauge<long> IngestionFailureStreak =
        Meter.CreateGauge<long>(
            "saydin.ingestion.failure_streak",
            description: "Son authoritative başarıdan sonraki durable başarısız attempt sayısı");

    public static readonly Counter<long> IngestionAttempts =
        Meter.CreateCounter<long>(
            "saydin.ingestion.attempts.total",
            description: "Terminal ingestion attempt sayısı");

    public static readonly Histogram<long> IngestionRecords =
        Meter.CreateHistogram<long>(
            "saydin.ingestion.records",
            description: "Attempt başına accepted/rejected kayıt sayısı");

    public static readonly Histogram<double> IngestionAttemptDuration =
        Meter.CreateHistogram<double>(
            "saydin.ingestion.attempt.duration.seconds",
            unit: "s",
            description: "Durable ingestion attempt süresi");

    public static readonly Gauge<long> MarketCalendarCoverageHorizon =
        Meter.CreateGauge<long>(
            "saydin.market_calendar.coverage.horizon.days",
            unit: "d",
            description: "Active authoritative calendar release coverage horizon'ı");

    /// <summary>
    /// F2.3-4 ([C-C-22]): Activity log batch yazımının başarısız satır sayısı.
    /// Tag: outcome="retry_exhausted|cancelled|toxic_row|fatal_contract".
    /// Operasyon ekibi observability boşluğu için bu sayaca dayanır — kaybedilen
    /// veya fail-fast öncesi yazılamayan log sayısı görünür kalır.
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
    /// Channel writer tamamlandığı için producer tarafından reddedilen activity log sayısı.
    /// Capacity drop değildir. Tag'ler: action (allowlist), reason="writer_completed".
    /// </summary>
    public static readonly Counter<long> ActivityLogQueueRejectedWrites =
        Meter.CreateCounter<long>(
            "saydin.activity_log.queue.rejected_writes.total",
            description: "Tamamlanmış channel writer tarafından reddedilen activity log sayısı");

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

    /// <summary>
    /// Security admission kararları. Tag'ler yalnız sabit allowlist değerleridir:
    /// bucket, outcome ve reason. IP, ağ pseudonym'i, principal veya Redis key taşınmaz.
    /// </summary>
    public static readonly Counter<long> SecurityAdmissionDecisions =
        Meter.CreateCounter<long>(
            "saydin.security.admission.decisions.total",
            description: "Dağıtık güvenlik admission kararları (düşük kardinaliteli nedenlerle)");

    /// <summary>
    /// Prometheus sözleşmesindeki kayıp sayaçlarını provider başladıktan sonra sıfır
    /// değerle materyalize eder. Böylece canlı scrape admission'ı metric adını ve
    /// bounded label şemasını ilk gerçek kaybı beklemeden doğrulayabilir.
    /// </summary>
    public static void InitializeActivityLogContractSeries()
    {
        ActivityLogWriteFailures.Add(0,
            new KeyValuePair<string, object?>("outcome", "retry_exhausted"));
        ActivityLogQueueDrops.Add(0,
            new KeyValuePair<string, object?>("action", "other"));
        ActivityLogQueueRejectedWrites.Add(0,
            new KeyValuePair<string, object?>("action", "other"),
            new KeyValuePair<string, object?>("reason", "writer_completed"));
    }
}
