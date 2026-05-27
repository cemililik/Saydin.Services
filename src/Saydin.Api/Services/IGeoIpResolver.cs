using System.Net;

namespace Saydin.Api.Services;

/// <summary>
/// IP adresinden coğrafi konum bilgisi çözer.
/// </summary>
public interface IGeoIpResolver
{
    /// <summary>
    /// Çözümlenmiş ülke (ISO 3166-1 alpha-2) ve şehir bilgisini taşıyan tuple döner.
    /// İstemci IP'si null, loopback/private veya MaxMind veritabanında bulunamadıysa
    /// her iki alan da <c>null</c> olur (tuple kendisi <c>null</c> dönmez).
    /// </summary>
    (string? CountryCode, string? City) Resolve(IPAddress? ip);
}
