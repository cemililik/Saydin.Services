using System.Threading.Channels;
using Saydin.Shared.Entities;

namespace Saydin.Api.BackgroundServices;

/// <summary>
/// Closes activity-log ingress as a distinct hosted-service phase. It is
/// registered after <see cref="ActivityLogWriter"/>, so hosted services stop in
/// this order: Kestrel, channel ingress, writer. Consequently in-flight HTTP
/// requests finish producing before the writer drains the completed channel.
/// </summary>
internal sealed class ActivityLogChannelLifetime(Channel<ActivityLog> channel) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        channel.Writer.TryComplete();
        return Task.CompletedTask;
    }
}
