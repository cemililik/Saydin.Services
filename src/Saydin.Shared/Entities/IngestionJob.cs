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

    /// <summary>Hangi asset için bu işlemin yapıldığı.</summary>
    public Guid AssetId { get; init; }

    /// <summary>
    /// İşlem tipi. CHECK constraint izin verilen değerler:
    /// <c>historical_backfill</c>, <c>daily_update</c>.
    /// Bkz. <see cref="IngestionJobTypes"/>.
    /// </summary>
    public string JobType { get; init; } = default!;

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

    // Navigation
    public Asset? Asset { get; init; }
}

/// <summary>Ingestion job tip sabitleri (DB CHECK constraint ile eşleşir).</summary>
public static class IngestionJobTypes
{
    public const string HistoricalBackfill = "historical_backfill";
    public const string DailyUpdate = "daily_update";
}

/// <summary>Ingestion job durum sabitleri (DB CHECK constraint ile eşleşir).</summary>
public static class IngestionJobStatuses
{
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
}
