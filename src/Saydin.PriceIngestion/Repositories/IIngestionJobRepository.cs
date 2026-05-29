using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

/// <summary>
/// `ingestion_jobs` tablosuna yaşam döngüsü kayıtları yazan repository.
///
/// Kullanım:
/// <code>
/// var job = await jobs.StartAsync(assetId, JobTypes.HistoricalBackfill, from, to, source, ct);
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
    /// INGR-002: <paramref name="assetId"/> inflation (EVDS) job'larında <c>null</c> olur;
    /// <paramref name="source"/> veri kaynağını (provenance) belirtir.
    /// </summary>
    Task<IngestionJob> StartAsync(
        Guid? assetId,
        string jobType,
        DateOnly? rangeStart,
        DateOnly? rangeEnd,
        string? source,
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
