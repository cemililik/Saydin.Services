using System.Threading.Channels;
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
            logger.LogWarning("Activity log kuyruğu dolu, kayıt düşürüldü: {Action}", entry.Action);
        }
    }
}
