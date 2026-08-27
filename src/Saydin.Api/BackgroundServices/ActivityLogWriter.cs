using System.Threading.Channels;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.BackgroundServices;

public sealed class ActivityLogWriter(
    Channel<ActivityLog> channel,
    IActivityLogBatchStore store,
    ILogger<ActivityLogWriter> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(20);
    private CancellationToken shutdownDeadline;

    // F2.3-4 ([C-C-22]): in-process retry. SaveChangesAsync transient failure'larında
    // 2 ek deneme ile batch'i kurtarmaya çalışır. Toplam attempts = 3 (ilk + 2 retry).
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(200);

    // LOGR-009: Bisection eşiği — tek satır altında bisection yapılmaz; satır izole edilir.
    private const int BisectMinBatch = 1;

    // Sonar S1192 + F4 follow-up: Metric tag adı tek source-of-truth — literal repeat yok.
    private const string OutcomeTagKey       = "outcome";
    private const string OutcomeRetryExhaust  = "retry_exhausted";
    private const string OutcomeToxicRow      = "toxic_row";
    private const string OutcomeFatalContract = "fatal_contract";
    private const string OutcomeWriterDead     = "writer_dead";
    private const string OutcomeShutdownAbandoned = "shutdown_abandoned";

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
        catch (Exception ex)
        {
            // Stop accepting new rows immediately when the sole reader dies. The
            // fatal batch is already accounted for by FlushAsync; rows still
            // queued behind it are a separate, otherwise invisible loss.
            channel.Writer.TryComplete(ex);
            var queued = channel.Reader.Count;
            if (queued > 0) ReportFailure(queued, OutcomeWriterDead);
            throw;
        }

        await DrainRemainingAsync(buffer);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        shutdownDeadline = cancellationToken;
        // ActivityLogChannelLifetime closes ingress after Kestrel has drained.
        // This service only waits for the already-completed channel to flush.
        logger.LogInformation("ActivityLogWriter duruyor — tamamlanmış channel drain ediliyor");
        return base.StopAsync(cancellationToken);
    }

    private async Task DrainRemainingAsync(List<ActivityLog> buffer)
    {
        // 30s timeout: shutdown drain için üst sınır.
        using var timeout = new CancellationTokenSource(ShutdownDrainTimeout);
        using var cts = shutdownDeadline.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, shutdownDeadline)
            : CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
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
            var abandoned = buffer.Count + channel.Reader.Count;
            if (abandoned > 0) ReportFailure(abandoned, OutcomeShutdownAbandoned);
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
                case null:
                    return;

                case ActivityLogWriteFailureKind.Cancelled:
                    throw outcome.Exception!;

                case ActivityLogWriteFailureKind.ToxicRow:
                    lastException = outcome.Exception;
                    if (await HandleToxicAsync(entries, isShutdown, ct))
                        return; // bisection geri kalanı kurtardı
                    break;

                case ActivityLogWriteFailureKind.TransientBatch:
                    lastException = outcome.Exception;
                    if (attempt >= maxAttempts) break;
                    if (!await BackoffAsync(attempt, maxAttempts, entries.Count, outcome.Exception!, ct))
                        throw new OperationCanceledException(ct);
                    break;

                case ActivityLogWriteFailureKind.FatalHost:
                    ReportFailure(entries.Count, OutcomeFatalContract);
                    logger.LogCritical(outcome.Exception,
                        "Activity log writer systemic veritabanı hatasıyla duruyor");
                    throw outcome.Exception!;

                // Sonar S131: default — enum'a yeni değer eklendiğinde derleyici
                // switch'in eksikliğini sezmez. Runtime'da fail-fast vermesi için
                // explicit branch; pratikte unreachable.
                default:
                    throw new InvalidOperationException(
                        $"Unhandled FlushOutcomeKind: {outcome.Kind}");
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
            await store.SaveAsync(entries, ct);
            return new FlushOutcome(null, null);
        }
        catch (Exception ex)
        {
            return new FlushOutcome(ActivityLogWriteFailureClassifier.Classify(ex), ex);
        }
    }

    /// <summary>LOGR-009: toxic message → bisect; tek satırlık batch → izoleli drop + metric.</summary>
    private async Task<bool> HandleToxicAsync(List<ActivityLog> entries, bool isShutdown, CancellationToken ct)
    {
        if (entries.Count > BisectMinBatch)
        {
            logger.LogWarning(
                "Activity log batch toxic message şüphesi — bisection ile {Count} kayıt bölünüyor",
                entries.Count);
            await BisectAndFlushAsync(entries, isShutdown, ct);
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
    private async Task BisectAndFlushAsync(
        List<ActivityLog> entries, bool isShutdown, CancellationToken ct)
    {
        if (entries.Count <= BisectMinBatch)
        {
            await FlushAsync(entries, isShutdown, ct);
            return;
        }

        var mid = entries.Count / 2;
        await FlushAsync(entries.GetRange(0, mid), isShutdown, ct);
        await FlushAsync(entries.GetRange(mid, entries.Count - mid), isShutdown, ct);
    }

    private readonly record struct FlushOutcome(
        ActivityLogWriteFailureKind? Kind, Exception? Exception);
}
