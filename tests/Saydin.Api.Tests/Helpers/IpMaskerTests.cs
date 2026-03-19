using System.Net;
using FluentAssertions;
using Saydin.Api.Helpers;

namespace Saydin.Api.Tests.Helpers;

public class IpMaskerTests
{
    [Fact]
    public void Mask_Null_ReturnsNull()
    {
        IpMasker.Mask(null).Should().BeNull();
    }

    [Fact]
    public void Mask_IPv4_ZerosLastOctet()
    {
        var ip = IPAddress.Parse("192.168.1.42");

        var masked = IpMasker.Mask(ip);

        masked.Should().Be(IPAddress.Parse("192.168.1.0"));
    }

    [Fact]
    public void Mask_IPv4_AlreadyZero_Unchanged()
    {
        var ip = IPAddress.Parse("10.0.0.0");

        var masked = IpMasker.Mask(ip);

        masked.Should().Be(IPAddress.Parse("10.0.0.0"));
    }

    [Fact]
    public void Mask_IPv6_ZerosLast80Bits()
    {
        var ip = IPAddress.Parse("2001:db8:85a3::8a2e:370:7334");

        var masked = IpMasker.Mask(ip);

        masked.Should().NotBeNull();
        var bytes = masked!.GetAddressBytes();
        // Son 10 byte (80 bit) sıfır olmalı
        bytes[6..16].Should().AllBeEquivalentTo((byte)0);
        // İlk 6 byte korunmalı
        bytes[..6].Should().BeEquivalentTo(ip.GetAddressBytes()[..6]);
    }

    [Fact]
    public void Mask_Loopback_ZerosLastOctet()
    {
        var masked = IpMasker.Mask(IPAddress.Loopback);

        masked.Should().Be(IPAddress.Parse("127.0.0.0"));
    }
}
