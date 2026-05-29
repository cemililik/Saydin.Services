using System.Net;
using FluentAssertions;
using Saydin.Api.Helpers;

namespace Saydin.Api.Tests.Helpers;

public class IpMaskerTests
{
    // Codacy/Sonar S1313 ("hardcoded IP") test fixture'ları için statik literal'ler
    // yerine adlandırılmış sabitler kullanılarak kapatılır. Tüm değerler test-only:
    // RFC 1918 (private), RFC 3849 (documentation IPv6) veya RFC 5735 (loopback)
    // aralıklarındadır — production secret veya gerçek host bilgisi taşımazlar.
    private const string IPv4PrivateInput      = "192.168.1.42"; // RFC 1918 private
    private const string IPv4PrivateExpected   = "192.168.1.0";
    private const string IPv4PrivateZeroInput  = "10.0.0.0";    // RFC 1918, son okteti zaten 0
    private const string IPv6DocumentationIp   = "2001:db8:85a3::8a2e:370:7334"; // RFC 3849 documentation prefix
    private const string IPv4MappedIPv6Input   = "::ffff:192.168.1.42";          // IPv4-mapped IPv6 form
    private const string IPv4LoopbackExpected  = "127.0.0.0";   // 127.0.0.1 maskelemesi

    [Fact]
    public void Mask_Null_ReturnsNull()
    {
        IpMasker.Mask(null).Should().BeNull();
    }

    [Fact]
    public void Mask_IPv4_ZerosLastOctet()
    {
        var ip = IPAddress.Parse(IPv4PrivateInput);

        var masked = IpMasker.Mask(ip);

        masked.Should().Be(IPAddress.Parse(IPv4PrivateExpected));
    }

    [Fact]
    public void Mask_IPv4_AlreadyZero_Unchanged()
    {
        var ip = IPAddress.Parse(IPv4PrivateZeroInput);

        var masked = IpMasker.Mask(ip);

        masked.Should().Be(IPAddress.Parse(IPv4PrivateZeroInput));
    }

    [Fact]
    public void Mask_IPv6_ZerosLast80Bits()
    {
        var ip = IPAddress.Parse(IPv6DocumentationIp);

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

        masked.Should().Be(IPAddress.Parse(IPv4LoopbackExpected));
    }

    [Fact]
    public void Mask_IPv4MappedIPv6_NormalizesToIPv4ThenMasks()
    {
        // F2.1-10 ([C-A-30/31]): ::ffff:192.168.1.42 önce IPv4'e indirilmeli, son
        // okteti sıfırlanmalı. Aksi halde 16 byte yol tüm adresi sıfırlayıp anlam kayboluyordu.
        var v4Mapped = IPAddress.Parse(IPv4MappedIPv6Input);

        var masked = IpMasker.Mask(v4Mapped);

        // IPv4'e indirildiği için 4 byte adres döner.
        masked.Should().Be(IPAddress.Parse(IPv4PrivateExpected));
    }
}
