using System.Threading.Channels;
using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

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
