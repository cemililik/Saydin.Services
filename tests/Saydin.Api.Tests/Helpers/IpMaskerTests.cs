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

    [Fact]
    public void Mask_IPv4MappedIPv6_NormalizesToIPv4ThenMasks()
    {
        // F2.1-10 ([C-A-30/31]): ::ffff:192.168.1.42 önce IPv4'e indirilmeli, son
        // okteti sıfırlanmalı. Aksi halde 16 byte yol tüm adresi sıfırlayıp anlam kayboluyordu.
        // Codacy/SonarQube IP sabit kullanımına S1313 verir; test fixture sabitleri
        // production secret değildir — RFC 5735 §3 / RFC 6890 doc IP range'i kullanılır.
        var v4Mapped = IPAddress.Parse("::ffff:192.168.1.42"); // NOSONAR S1313 — test fixture, RFC 1918 private

        var masked = IpMasker.Mask(v4Mapped);

        // IPv4'e indirildiği için 4 byte adres döner.
        masked.Should().Be(IPAddress.Parse("192.168.1.0"));
    }
}
