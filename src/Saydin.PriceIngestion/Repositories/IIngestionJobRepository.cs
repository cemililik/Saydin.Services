using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

/// <summary>
/// `ingestion_jobs` tablosuna yaşam döngüsü kayıtları yazan repository.
///
/// Kullanım:
/// <code>
/// var job = await jobs.StartAsync(assetId, JobTypes.HistoricalBackfill, from, to, ct);
/// try
/// {
///     int upserted = ...; // ingestion işi
///     await jobs.MarkSuccessAsync(job.Id, upserted, ct);
/// }
/// catch (Exception ex)
/// {
///     await jobs.MarkFailedAsync(job.Id, ex.Message, ct);
///     throw;
/// }
/// </code>
/// </summary>
public interface IIngestionJobRepository
{
    /// <summary>
    /// Yeni bir ingestion job açar (status=running) ve DB'ye yazıp döner.
    /// </summary>
    Task<IngestionJob> StartAsync(
        Guid assetId,
        string jobType,
        DateOnly? rangeStart,
        DateOnly? rangeEnd,
        CancellationToken ct);

    /// <summary>
    /// Job'ı başarılı olarak işaretler. <paramref name="recordsUpserted"/> kayıt sayısı.
    /// </summary>
    Task MarkSuccessAsync(Guid jobId, int recordsUpserted, CancellationToken ct);

    /// <summary>
    /// Job'ı başarısız olarak işaretler. <paramref name="errorMessage"/> kısa açıklama (truncate ile).
    /// </summary>
    Task MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct);
}
