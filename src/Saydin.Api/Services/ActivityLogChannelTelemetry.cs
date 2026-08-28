using Saydin.Shared.Constants;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

/// <summary>
/// Activity channel kayıplarının tek telemetry sahibidir. DropWrite callback'i gerçek
/// capacity drop'unu, producer'daki TryWrite=false yolu ise completed/rejected write'ı
/// kaydeder. Warning'ler metric'ten bağımsız olarak bounded tutulur.
/// </summary>
public sealed class ActivityLogChannelTelemetry(
    TimeProvider timeProvider,
    ILogger<ActivityLogChannelTelemetry> logger)
{
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private long _nextDropWarningUtcTicks;
    private long _nextRejectedWarningUtcTicks;

    public void RecordDropped(ActivityLog entry)
    {
        var actionTag = NormalizeAction(entry.Action);
        SaydinMetrics.ActivityLogQueueDrops.Add(
            1,
            new KeyValuePair<string, object?>("action", actionTag));

        if (TryReserveWarning(ref _nextDropWarningUtcTicks))
        {
            logger.LogWarning(
                "Activity log kuyruğu dolu; kayıt DropWrite tarafından düşürüldü: {Action}",
                actionTag);
        }
    }

    public void RecordRejected(ActivityLog entry)
    {
        var actionTag = NormalizeAction(entry.Action);
        SaydinMetrics.ActivityLogQueueRejectedWrites.Add(
            1,
            new KeyValuePair<string, object?>("action", actionTag),
            new KeyValuePair<string, object?>("reason", "writer_completed"));

        if (TryReserveWarning(ref _nextRejectedWarningUtcTicks))
        {
            logger.LogWarning(
                "Activity log yazımı reddedildi; channel writer tamamlanmış: {Action}",
                actionTag);
        }
    }

    private bool TryReserveWarning(ref long nextWarningUtcTicks)
    {
        var nowTicks = timeProvider.GetUtcNow().UtcTicks;

        while (true)
        {
            var nextTicks = Volatile.Read(ref nextWarningUtcTicks);
            if (nowTicks < nextTicks)
                return false;

            var candidate = nowTicks > DateTimeOffset.MaxValue.UtcTicks - WarningInterval.Ticks
                ? DateTimeOffset.MaxValue.UtcTicks
                : nowTicks + WarningInterval.Ticks;
            if (Interlocked.CompareExchange(ref nextWarningUtcTicks, candidate, nextTicks) == nextTicks)
                return true;
        }
    }

    private static string NormalizeAction(string? action) =>
        action is not null && ActivityActions.Lookup.Contains(action) ? action : "unknown";
}
