using System.Threading.Channels;
using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

/// <summary>
/// Channel-backed activity logger. <see cref="BoundedChannelFullMode.DropWrite"/> capacity
/// drop'unda <see cref="ChannelWriter{T}.TryWrite"/> true döner; gerçek drop telemetry'si
/// channel'ın itemDropped callback'indedir. Buradaki false yalnız completed/rejected writer
/// semantiğidir.
/// </summary>
public sealed class ChannelActivityLogger(
    Channel<ActivityLog> channel,
    ActivityLogChannelTelemetry telemetry) : IActivityLogger
{
    public void Log(ActivityLog entry)
    {
        if (!channel.Writer.TryWrite(entry))
            telemetry.RecordRejected(entry);
    }
}
