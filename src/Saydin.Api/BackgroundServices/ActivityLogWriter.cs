using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Data;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.BackgroundServices;

public sealed class ActivityLogWriter(
    Channel<ActivityLog> channel,
    IServiceScopeFactory scopeFactory,
    ILogger<ActivityLogWriter> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(30);

    // F2.3-4 ([C-C-22]): in-process retry. SaveChangesAsync transient failure'larında
    // 2 ek deneme ile batch'i kurtarmaya çalışır. Toplam attempts = 3 (ilk + 2 retry).
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(200);

    // LOGR-009: Bisection eşiği — tek satır altında bisection yapılmaz; satır izole edilir.
    private const int BisectMinBatch = 1;

    // Sonar S1192 + F4 follow-up: Metric tag adı tek source-of-truth — literal repeat yok.
    private const string OutcomeTagKey       = "outcome";
    private const string OutcomeCancelled    = "cancelled";
    private const string OutcomeRetryExhaust = "retry_exhausted";
    private const string OutcomeToxicRow     = "toxic_row";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ActivityLogWriter başlatıldı");

        var buffer = new List<ActivityLog>(BatchSize);

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(stoppingToken))
            {
                buffer.Add(entry);

                while (buffer.Count < BatchSize && channel.Reader.TryRead(out var extra))
                    buffer.Add(extra);

                await FlushAsync(buffer, isShutdown: false, stoppingToken);
                buffer.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — kalan kayıtlar DrainRemainingAsync ile batch'ler hâlinde yazılır.
        }

        await DrainRemainingAsync(buffer);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("ActivityLogWriter duruyor — channel writer kapatılıyor");
        channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private async Task DrainRemainingAsync(List<ActivityLog> buffer)
    {
        // 30s timeout: shutdown drain için üst sınır.
        using var cts = new CancellationTokenSource(ShutdownDrainTimeout);
        try
        {
            while (channel.Reader.TryRead(out var extra))
            {
                buffer.Add(extra);
                if (buffer.Count >= BatchSize)
                {
                    // LOGR-012: Shutdown path'inde retry yapılmaz (timeout sığsın diye).
                    await FlushAsync(buffer, isShutdown: true, cts.Token);
                    buffer.Clear();
                }
            }
            if (buffer.Count > 0)
                await FlushAsync(buffer, isShutdown: true, cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex,
                "ActivityLogWriter shutdown drain timeout aşıldı ({Timeout}s); {Remaining} kayıt yazılamadı",
                ShutdownDrainTimeout.TotalSeconds, buffer.Count + channel.Reader.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ActivityLogWriter shutdown sırasında {Count} kayıt drain edilemedi", buffer.Count);
        }
        finally
        {
            buffer.Clear();
        }
    }

    /// <summary>
    /// LOGR-009 + Sonar S3776: Refactor — yüksek kompleksite (20) try/catch'lerin
    /// içinde bisect/retry/backoff kararlarının iç içe geçmesinden kaynaklanıyordu.
    /// `TrySaveBatchAsync` ham DB write'ı izole eder; bu method yalnızca attempt
    /// yönetimi + outcome sınıflandırması yapar.
    /// </summary>
    private async Task FlushAsync(List<ActivityLog> entries, bool isShutdown, CancellationToken ct)
    {
        var maxAttempts = isShutdown ? 1 : MaxAttempts;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var outcome = await TrySaveBatchAsync(entries, ct);
            switch (outcome.Kind)
            {
                case FlushOutcomeKind.Success:
                    return;

                case FlushOutcomeKind.Cancelled:
                    ReportFailure(entries.Count, OutcomeCancelled);
                    throw outcome.Exception!;

                case FlushOutcomeKind.Toxic:
                    lastException = outcome.Exception;
                    if (await HandleToxicAsync(entries, isShutdown, ct))
                        return; // bisection geri kalanı kurtardı
                    break;

                case FlushOutcomeKind.Transient:
                    lastException = outcome.Exception;
                    if (attempt >= maxAttempts) break;
                    if (!await BackoffAsync(attempt, maxAttempts, entries.Count, outcome.Exception!, ct))
                        throw new OperationCanceledException(ct);
                    break;
            }
        }

        ReportFailure(entries.Count, OutcomeRetryExhaust);
        logger.LogError(lastException,
            "Activity log yazımı {Attempts} denemeden sonra başarısız. {Count} kayıt düşürüldü",
            maxAttempts, entries.Count);
    }

    /// <summary>Tek deneme — başarılı/cancelled/toxic/transient olarak sınıflandırır.</summary>
    private async Task<FlushOutcome> TrySaveBatchAsync(List<ActivityLog> entries, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SaydinDbContext>();
            await db.ActivityLogs.AddRangeAsync(entries, ct);
            await db.SaveChangesAsync(ct);
            return new FlushOutcome(FlushOutcomeKind.Success, null);
        }
        catch (OperationCanceledException ex)
        {
            return new FlushOutcome(FlushOutcomeKind.Cancelled, ex);
        }
        catch (DbUpdateException ex)
        {
            return new FlushOutcome(FlushOutcomeKind.Toxic, ex);
        }
        catch (Exception ex)
        {
            return new FlushOutcome(FlushOutcomeKind.Transient, ex);
        }
    }

    /// <summary>LOGR-009: toxic message → bisect; tek satırlık batch → izoleli drop + metric.</summary>
    private async Task<bool> HandleToxicAsync(List<ActivityLog> entries, bool isShutdown, CancellationToken ct)
    {
        if (entries.Count > BisectMinBatch && !isShutdown)
        {
            logger.LogWarning(
                "Activity log batch toxic message şüphesi — bisection ile {Count} kayıt bölünüyor",
                entries.Count);
            await BisectAndFlushAsync(entries, ct);
            return true;
        }
        if (entries.Count == BisectMinBatch)
        {
            ReportFailure(1, OutcomeToxicRow);
            logger.LogError(
                "Activity log toxic row düşürüldü. Action={Action}, DeviceId={DeviceId}",
                entries[0].Action, entries[0].DeviceId);
            return true; // izole; bisection seviyesinde "kurtarıldı" sayılır
        }
        return false;
    }

    private async Task<bool> BackoffAsync(int attempt, int maxAttempts, int count, Exception ex, CancellationToken ct)
    {
        logger.LogWarning(ex,
            "Activity log batch yazımı başarısız (deneme {Attempt}/{Max}). {Count} kayıt için tekrar denenecek",
            attempt, maxAttempts, count);

        // Exponential backoff: 200ms, 400ms.
        var delay = RetryBaseDelay * (1 << (attempt - 1));
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            ReportFailure(count, OutcomeCancelled);
            return false;
        }
    }

    private static void ReportFailure(int count, string outcome) =>
        SaydinMetrics.ActivityLogWriteFailures.Add(count,
            new KeyValuePair<string, object?>(OutcomeTagKey, outcome));

    /// <summary>
    /// LOGR-009: Batch toxic row ihtimaliyle bölünür; her yarım ayrı yazılır.
    /// Worst-case O(log N) attempt başına — 50 batch için ≤6 seviyeli ağaç.
    /// </summary>
    private async Task BisectAndFlushAsync(List<ActivityLog> entries, CancellationToken ct)
    {
        if (entries.Count <= BisectMinBatch)
        {
            await FlushAsync(entries, isShutdown: false, ct);
            return;
        }

        var mid = entries.Count / 2;
        await FlushAsync(entries.GetRange(0, mid), isShutdown: false, ct);
        await FlushAsync(entries.GetRange(mid, entries.Count - mid), isShutdown: false, ct);
    }

    private enum FlushOutcomeKind { Success, Cancelled, Toxic, Transient }

    private readonly record struct FlushOutcome(FlushOutcomeKind Kind, Exception? Exception);
}
