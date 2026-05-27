using System.Threading.Channels;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.BackgroundServices;

public sealed class ActivityLogWriter(
    Channel<ActivityLog> channel,
    IServiceScopeFactory scopeFactory,
    ILogger<ActivityLogWriter> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(30);

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
        catch (OperationCanceledException)
        {
            logger.LogWarning(
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
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SaydinDbContext>();
            await db.ActivityLogs.AddRangeAsync(entries, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Activity log yazımı başarısız. {Count} kayıt düşürüldü", entries.Count);
        }
    }
}
