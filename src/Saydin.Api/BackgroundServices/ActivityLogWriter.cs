using System.Threading.Channels;
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

                await FlushAsync(buffer, stoppingToken);
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
                    await FlushAsync(buffer, cts.Token);
                    buffer.Clear();
                }
            }
            if (buffer.Count > 0)
                await FlushAsync(buffer, cts.Token);
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

    private async Task FlushAsync(List<ActivityLog> entries, CancellationToken ct)
    {
        // F2.3-4: Retry with exponential backoff. Idempotent insert: ActivityLog.Id
        // entity-side oluşturulur (Guid.CreateVersion7) — aynı batch'in iki yazımı
        // PK çakışması ile reddedilir, sessiz duplicate riski yok.
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
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
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                lastException = ex;
                logger.LogWarning(ex,
                    "Activity log batch yazımı başarısız (deneme {Attempt}/{Max}). {Count} kayıt için tekrar denenecek",
                    attempt, MaxAttempts, entries.Count);

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
            MaxAttempts, entries.Count);
    }
}
