using System.Threading.Channels;
using Saydin.Shared.Constants;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

/// <summary>
/// Channel-backed activity logger. Channel <see cref="BoundedChannelFullMode.DropWrite"/>
/// modunda yapılandırıldığı için kuyruk dolduğunda <see cref="ChannelWriter{T}.TryWrite"/>
/// false döner — bu sayede düşürülen kayıt log ile görünür kılınır. (DropOldest modu
/// her zaman true döndüğü için drop telemetri'si imkansızdı.)
/// </summary>
public sealed class ChannelActivityLogger(
    Channel<ActivityLog> channel,
    ILogger<ChannelActivityLogger> logger) : IActivityLogger
{
    public void Log(ActivityLog entry)
    {
        if (!channel.Writer.TryWrite(entry))
        {
            // F2.2-15 / F2.2-24 + LOGR-002: Drop sayısı counter metric'e işlenir.
            // Action tag whitelist'e tabi tutulur — bilinmeyen action gelirse "unknown"
            // fallback ile yazılır, Prometheus tag cardinality fixed kümede kalır
            // (~12 değer); dev'in keyfi action string'i metric explosion'a yol açmaz.
            var actionTag = ActivityActions.Lookup.Contains(entry.Action) ? entry.Action : "unknown";
            SaydinMetrics.ActivityLogQueueDrops.Add(1, new KeyValuePair<string, object?>("action", actionTag));
            logger.LogWarning("Activity log kuyruğu dolu, kayıt düşürüldü: {Action}", entry.Action);
        }
    }
}
