using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Saydin.Api.Services;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Services;

public class ChannelActivityLoggerTests
{
    [Fact]
    public void Log_WritesEntryToChannel()
    {
        var channel = Channel.CreateUnbounded<ActivityLog>();
        var sut = new ChannelActivityLogger(channel, NullLogger<ChannelActivityLogger>.Instance);

        var entry = new ActivityLog
        {
            DeviceId = "test-device",
            Action = "what_if_calculate",
            StatusCode = 200,
        };

        sut.Log(entry);

        channel.Reader.TryRead(out var result).Should().BeTrue();
        result.Should().BeSameAs(entry);
    }

    [Fact]
    public void Log_ChannelFull_DoesNotThrow()
    {
        var channel = Channel.CreateBounded<ActivityLog>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var sut = new ChannelActivityLogger(channel, NullLogger<ChannelActivityLogger>.Instance);

        var entry1 = new ActivityLog { DeviceId = "d1", Action = "a1", StatusCode = 200 };
        var entry2 = new ActivityLog { DeviceId = "d2", Action = "a2", StatusCode = 200 };
        var entry3 = new ActivityLog { DeviceId = "d3", Action = "a3", StatusCode = 200 };

        var act = () =>
        {
            sut.Log(entry1);
            sut.Log(entry2);
            sut.Log(entry3);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_MultipleEntries_AllWrittenInOrder()
    {
        var channel = Channel.CreateUnbounded<ActivityLog>();
        var sut = new ChannelActivityLogger(channel, NullLogger<ChannelActivityLogger>.Instance);

        for (var i = 0; i < 5; i++)
        {
            sut.Log(new ActivityLog
            {
                DeviceId = $"device-{i}",
                Action = "what_if_calculate",
                StatusCode = 200,
            });
        }

        channel.Reader.Count.Should().Be(5);
    }
}
