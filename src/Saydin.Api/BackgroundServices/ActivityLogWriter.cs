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

    // F2.3-4 ([C-C-22]): Polly bağımlılığı eklemek yerine basit in-process retry.
    // SaveChangesAsync transient failure'larında (deadlock, connection blink) 2 ek
    // deneme ile batch'i kurtarmaya çalışır. Toplam attempts = 3 (ilk + 2 retry).
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(200);

    // LOGR-009: Bisection eşiği — bu boyutun altına düşersek tek satırı izole
    // ederiz; her batch'i 1'e kadar bölmek gerek değil.
    private const int BisectMinBatch = 1;

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
        // Shutdown'da hangi DB call'un takıldığını bilmiyoruz → 30s timeout.
        // Drain batch'ler hâlinde yapılır: tek seferde tüm kuyruğu memory'ye almak
        // 10k boundedchannel + spike senaryosunda MB'larca heap → batch'lere böl.
        using var cts = new CancellationTokenSource(ShutdownDrainTimeout);
        try
        {
            while (channel.Reader.TryRead(out var extra))
            {
                buffer.Add(extra);
                if (buffer.Count >= BatchSize)
                {
                    // LOGR-012: Shutdown path'inde retry yapma (`isShutdown: true`) —
                    // 50 batch × 3 attempt × 600ms backoff = 90s teorik worst case.
                    // 30s drain pencerede yetmez → kayıt sayısı düşer. Shutdown'da tek deneme.
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

    private async Task FlushAsync(List<ActivityLog> entries, bool isShutdown, CancellationToken ct)
    {
        // F2.3-4 + LOGR-012: Retry with exponential backoff. Shutdown path'inde tek deneme.
        // Idempotent insert: ActivityLog.Id entity-side oluşturulur (Guid.CreateVersion7)
        // — aynı batch'in iki yazımı PK çakışması ile reddedilir, sessiz duplicate riski yok.
        var maxAttempts = isShutdown ? 1 : MaxAttempts;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SaydinDbContext>();
                await db.ActivityLogs.AddRangeAsync(entries, ct);
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                // Shutdown / token cancel — retry yapmadan kuyruğu işle.
                SaydinMetrics.ActivityLogWriteFailures.Add(entries.Count,
                    new KeyValuePair<string, object?>("outcome", "cancelled"));
                throw;
            }
            catch (DbUpdateException dbex)
            {
                // LOGR-009: Toxic message — CHECK ihlali, length, FK gibi pre-validation
                // kaçırılan satır var. Tek retry sonrası bisection: batch'i ikiye böl,
                // her yarımı ayrı yazmaya çalış; zehirli satır izole edilir, geri kalan
                // kurtarılır. Retry'sız direkt bisection daha hızlı ama transient
                // (deadlock) ile poison'u ayırt edemiyoruz — ilk deneme retry, sonraki bisection.
                lastException = dbex;
                if (entries.Count > BisectMinBatch && !isShutdown)
                {
                    logger.LogWarning(dbex,
                        "Activity log batch toxic message şüphesi — bisection ile {Count} kayıt bölünüyor",
                        entries.Count);
                    await BisectAndFlushAsync(entries, ct);
                    return;
                }
                // Tek satırlık batch zaten poison — sonraki attempt fark etmez.
                if (entries.Count == BisectMinBatch)
                {
                    SaydinMetrics.ActivityLogWriteFailures.Add(1,
                        new KeyValuePair<string, object?>("outcome", "toxic_row"));
                    logger.LogError(dbex,
                        "Activity log toxic row düşürüldü. Action={Action}, DeviceId={DeviceId}",
                        entries[0].Action, entries[0].DeviceId);
                    return;
                }
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                logger.LogWarning(ex,
                    "Activity log batch yazımı başarısız (deneme {Attempt}/{Max}). {Count} kayıt için tekrar denenecek",
                    attempt, maxAttempts, entries.Count);

                // Exponential backoff: 200ms, 400ms.
                var delay = RetryBaseDelay * (1 << (attempt - 1));
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException)
                {
                    SaydinMetrics.ActivityLogWriteFailures.Add(entries.Count,
                        new KeyValuePair<string, object?>("outcome", "cancelled"));
                    throw;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        // Tüm denemeler tükendi — counter metric ile observability boşluğu kapatılır.
        SaydinMetrics.ActivityLogWriteFailures.Add(entries.Count,
            new KeyValuePair<string, object?>("outcome", "retry_exhausted"));
        logger.LogError(lastException,
            "Activity log yazımı {Attempts} denemeden sonra başarısız. {Count} kayıt düşürüldü",
            maxAttempts, entries.Count);
    }

    /// <summary>
    /// LOGR-009: Batch toxic row ihtimaliyle bölünür; her yarım ayrı yazılır. Recursive
    /// yapı (1) toxic satırı izole eder, (2) geri kalan kayıtları kurtarır. Worst-case
    /// O(log N) attempt başına — 50 batch için ≤6 seviyeli ağaç.
    /// </summary>
    private async Task BisectAndFlushAsync(List<ActivityLog> entries, CancellationToken ct)
    {
        if (entries.Count <= BisectMinBatch)
        {
            await FlushAsync(entries, isShutdown: false, ct);
            return;
        }

        var mid = entries.Count / 2;
        var first = entries.GetRange(0, mid);
        var second = entries.GetRange(mid, entries.Count - mid);

        await FlushAsync(first, isShutdown: false, ct);
        await FlushAsync(second, isShutdown: false, ct);
    }
}
