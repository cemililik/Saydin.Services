using System.Net;

namespace Saydin.Api.Helpers;

/// <summary>
/// IP adresinin son oktetini sıfırlayarak KVKK uyumlu maskeleme yapar.
/// IPv4: 192.168.1.42 → 192.168.1.0
/// IPv6: son 80 bit sıfırlanır.
/// IPv4-mapped IPv6 (::ffff:a.b.c.d) önce IPv4'e indirgenir; aksi halde
/// 16-byte yol tüm adresi sıfırlayıp anlamlı bilgi kaybeder.
/// </summary>
public static class IpMasker
{
    public static IPAddress? Mask(IPAddress? ip)
    {
        if (ip is null) return null;

        // IPv4-mapped IPv6 → IPv4: ::ffff:192.168.1.42 maskeleme öncesinde 192.168.1.42 olur,
        // sonuçta v4 son okteti sıfırlanır.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        var bytes = ip.GetAddressBytes();

        if (bytes.Length == 4)
        {
            bytes[3] = 0;
        }
        else if (bytes.Length == 16)
        {
            Array.Fill<byte>(bytes, 0, 6, 10);
        }

        return new IPAddress(bytes);
    }
}
