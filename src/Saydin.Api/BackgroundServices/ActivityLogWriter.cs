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

        await foreach (var entry in channel.Reader.ReadAllAsync(ct))
        {
            buffer.Add(entry);

            while (buffer.Count < BatchSize && channel.Reader.TryRead(out var extra))
                buffer.Add(extra);

            await FlushAsync(buffer, ct);
            buffer.Clear();
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
