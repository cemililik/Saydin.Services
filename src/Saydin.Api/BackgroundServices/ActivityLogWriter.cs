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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("ActivityLogWriter başlatıldı");

        var buffer = new List<ActivityLog>(BatchSize);

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct))
            {
                buffer.Add(entry);

                while (buffer.Count < BatchSize && channel.Reader.TryRead(out var extra))
                    buffer.Add(extra);

                await FlushAsync(buffer, ct);
                buffer.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — kalan kayıtları best-effort yaz, throw etme
            // (StopAsync zaten channel'ı complete etti, reader doğal şekilde tamamlanacaktı).
        }

        // ExecuteAsync döngüsünden çıkmadan önce kalan buffer'ı drain et;
        // production'da kayıp kayıt önemli (telemetri).
        await DrainRemainingAsync(buffer);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("ActivityLogWriter duruyor — channel writer kapatılıyor");
        // Writer.Complete() çağrılınca reader doğal şekilde tamamlanır;
        // ExecuteAsync await foreach döngüsünden çıkar, kalan kayıtlar drain edilir.
        channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private async Task DrainRemainingAsync(List<ActivityLog> buffer)
    {
        try
        {
            while (channel.Reader.TryRead(out var extra))
                buffer.Add(extra);
            if (buffer.Count > 0)
                await FlushAsync(buffer, CancellationToken.None);
            buffer.Clear();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ActivityLogWriter shutdown sırasında {Count} kayıt drain edilemedi", buffer.Count);
        }
    }

    private async Task FlushAsync(List<ActivityLog> entries, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SaydinDbContext>();
            db.ActivityLogs.AddRange(entries);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Activity log yazımı başarısız. {Count} kayıt düşürüldü", entries.Count);
        }
    }
}
