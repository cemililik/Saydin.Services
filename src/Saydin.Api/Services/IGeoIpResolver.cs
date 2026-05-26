using System.Net;

namespace Saydin.Api.Services;

/// <summary>
/// IP adresinden coğrafi konum bilgisi çözer.
/// </summary>
public interface IGeoIpResolver
{
    /// <summary>
    /// IP adresinden ülke (ISO 3166-1 alpha-2) ve şehir bilgisi döner.
    /// Çözümlenemezse null döner.
    /// </summary>
    (string? CountryCode, string? City) Resolve(IPAddress? ip);
}
