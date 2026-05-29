using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

/// <summary>
/// `ingestion_jobs` tablosuna EF Core üzerinden yazan repository.
/// BackgroundService singleton içinden çağrıldığı için <see cref="IDbContextFactory{TContext}"/>
/// kullanır (her operasyon kendi kısa ömürlü DbContext'ini açar).
/// </summary>
public sealed class IngestionJobRepository(IDbContextFactory<SaydinDbContext> contextFactory)
    : IIngestionJobRepository
{
    /// <summary>error_message kolonu TEXT — sınırsız ama log noise'a karşı truncate yaparız.</summary>
    private const int MaxErrorMessageLength = 2000;

    public async Task<IngestionJob> StartAsync(
        Guid? assetId,
        string jobType,
        DateOnly? rangeStart,
        DateOnly? rangeEnd,
        string? source,
        CancellationToken ct)
    {
        var job = new IngestionJob
        {
            AssetId        = assetId,
            JobType        = jobType,
            Source         = source,
            StartedAt      = DateTimeOffset.UtcNow,
            Status         = IngestionJobStatuses.Running,
            DateRangeStart = rangeStart,
            DateRangeEnd   = rangeEnd,
        };

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(ct);
        return job;
    }

    public async Task MarkSuccessAsync(Guid jobId, int recordsUpserted, CancellationToken ct)
    {
        await UpdateStatusAsync(jobId,
            status: IngestionJobStatuses.Success,
            recordsUpserted: recordsUpserted,
            errorMessage: null,
            ct);
    }

    public async Task MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct)
    {
        var truncated = errorMessage.Length > MaxErrorMessageLength
            ? errorMessage[..MaxErrorMessageLength]
            : errorMessage;

        await UpdateStatusAsync(jobId,
            status: IngestionJobStatuses.Failed,
            recordsUpserted: null,
            errorMessage: truncated,
            ct);
    }

    private async Task UpdateStatusAsync(
        Guid jobId,
        string status,
        int? recordsUpserted,
        string? errorMessage,
        CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var finishedAt = DateTimeOffset.UtcNow;

        // Tek SQL UPDATE — EF Core change tracking yerine targeted update.
        // ExecuteSqlInterpolatedAsync ile parametreli (SQL injection güvenli).
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE ingestion_jobs
               SET status            = {status},
                   finished_at       = {finishedAt},
                   records_upserted  = {recordsUpserted},
                   error_message     = {errorMessage}
             WHERE id = {jobId}
            """,
            ct);
    }
}
