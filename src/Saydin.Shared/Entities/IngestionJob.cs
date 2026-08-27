namespace Saydin.Shared.Entities;

/// <summary>
/// Bir veri çekme işleminin (backfill veya günlük güncelleme) yaşam döngüsü kaydı.
/// PriceIngestion worker'ları her ingestion siklusunun başında bir kayıt açar,
/// bitişte status'u <c>success</c> veya <c>failed</c> olarak güncelleyip
/// <see cref="RecordsUpserted"/> ve <see cref="ErrorMessage"/> alanlarını doldurur.
///
/// Tablo: <c>ingestion_jobs</c> (001_initial.sql).
/// </summary>
public sealed class IngestionJob
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Hangi asset için bu işlemin yapıldığı. INGR-002 (migration 012): inflation
    /// (EVDS) job'larında <c>null</c> — aylık TÜFE endeksi bir asset değildir.
    /// </summary>
    public Guid? AssetId { get; init; }

    /// <summary>
    /// İşlem tipi. CHECK constraint izin verilen değerler:
    /// <c>historical_backfill</c>, <c>daily_update</c>, <c>inflation_backfill</c>,
    /// <c>inflation_daily</c>. Bkz. <see cref="IngestionJobTypes"/>.
    /// </summary>
    public string JobType { get; init; } = default!;

    /// <summary>
    /// Veri kaynağı (provenance): <c>tcmb</c>, <c>coingecko</c>, <c>openexchangerates</c>,
    /// <c>twelvedata</c>, <c>evds</c>. INGR-002 (migration 012). Geçmiş satırlarda null olabilir.
    /// </summary>
    public string? Source { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// İşlem durumu. CHECK constraint izin verilen değerler:
    /// <c>running</c>, <c>success</c>, <c>failed</c>. Bkz. <see cref="IngestionJobStatuses"/>.
    /// </summary>
    public string Status { get; set; } = IngestionJobStatuses.Running;

    /// <summary>Başarıyla DB'ye yazılan kayıt sayısı (null = henüz tamamlanmadı).</summary>
    public int? RecordsUpserted { get; set; }

    /// <summary>Failed job'larda hata mesajı; success'te null.</summary>
    public string? ErrorMessage { get; set; }

    public DateOnly? DateRangeStart { get; init; }
    public DateOnly? DateRangeEnd { get; init; }

    /// <summary>Migration 015: durable logical window correlation; legacy rows remain null.</summary>
    public Guid? WindowId { get; init; }

    /// <summary>Stable machine-readable terminal outcome; legacy rows remain null.</summary>
    public string? OutcomeCode { get; set; }

    // Navigation
    public Asset? Asset { get; init; }
    public IngestionWindow? Window { get; init; }
}

/// <summary>Ingestion job tip sabitleri (DB CHECK constraint ile eşleşir; migration 011/012).</summary>
public static class IngestionJobTypes
{
    public const string HistoricalBackfill = "historical_backfill";
    public const string DailyUpdate = "daily_update";

    // INGR-002: EVDS (inflation) worker job tipleri — migration 011'de CHECK'e eklendi.
    public const string InflationBackfill = "inflation_backfill";
    public const string InflationDaily = "inflation_daily";
}

/// <summary>Ingestion job durum sabitleri (DB CHECK constraint ile eşleşir).</summary>
public static class IngestionJobStatuses
{
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
}
