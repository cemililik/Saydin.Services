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
    public void Log_ChannelFull_DropWriteMode_DoesNotThrow()
    {
        // DropWrite modunda kuyruk dolduğunda yeni entry düşer ve TryWrite false döner.
        // (Production'da bu yol Channel.CreateBounded(DropWrite) ile aktif — DropOldest
        // TryWrite'ı her zaman true döndürdüğü için drop telemetrisi imkansızdı.)
        var channel = Channel.CreateBounded<ActivityLog>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var sut = new ChannelActivityLogger(channel, NullLogger<ChannelActivityLogger>.Instance);

        var entry1 = new ActivityLog { DeviceId = "d1", Action = "a1", StatusCode = 200 };
        var entry2 = new ActivityLog { DeviceId = "d2", Action = "a2", StatusCode = 200 };

        var act = () =>
        {
            sut.Log(entry1);
            sut.Log(entry2);   // bu kayıt düşer, exception fırlatmaz
        };

        act.Should().NotThrow();
        channel.Reader.Count.Should().Be(1);
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

        var readBack = new List<ActivityLog>();
        while (channel.Reader.TryRead(out var entry))
            readBack.Add(entry);

        readBack.Should().HaveCount(5);
        readBack.Select(e => e.DeviceId).Should().Equal("device-0", "device-1", "device-2", "device-3", "device-4");
    }
}
