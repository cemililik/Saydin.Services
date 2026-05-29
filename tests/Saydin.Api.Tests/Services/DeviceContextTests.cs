using FluentAssertions;
using Saydin.Api.Services;

namespace Saydin.Api.Tests.Services;

/// <summary>
/// F2.2-3: scoped IDeviceContext sözleşmesi. Önceden servis-seviyesi "EmptyDeviceId"
/// testleri vardı; device id doğrulaması artık RequireDeviceId filter'ında. Bu testler
/// context'in kendi sözleşmesini (set edilmeden okuma → fırlatır) doğrular.
/// </summary>
public class DeviceContextTests
{
    [Fact]
    public void DeviceId_BeforeSet_ThrowsInvalidOperation()
    {
        var context = new DeviceContext();

        context.IsResolved.Should().BeFalse();
        var act = () => context.DeviceId;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetDeviceId_ThenDeviceId_ReturnsValue()
    {
        var context = new DeviceContext();

        context.SetDeviceId("device-123");

        context.IsResolved.Should().BeTrue();
        context.DeviceId.Should().Be("device-123");
    }

    [Fact]
    public void SetDeviceId_Twice_LastValueWins()
    {
        var context = new DeviceContext();

        context.SetDeviceId("first");
        context.SetDeviceId("second");

        context.DeviceId.Should().Be("second");
    }
}
