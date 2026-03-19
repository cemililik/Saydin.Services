using System.Net;

namespace Saydin.Api.Helpers;

/// <summary>
/// IP adresinin son oktetini sıfırlayarak KVKK uyumlu maskeleme yapar.
/// IPv4: 192.168.1.42 → 192.168.1.0
/// IPv6: son 80 bit sıfırlanır.
/// </summary>
public static class IpMasker
{
    public static IPAddress? Mask(IPAddress? ip)
    {
        if (ip is null) return null;

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
