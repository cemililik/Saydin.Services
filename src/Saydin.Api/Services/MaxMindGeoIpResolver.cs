using System.Net;
using MaxMind.GeoIP2;

namespace Saydin.Api.Services;

/// <summary>
/// MaxMind GeoLite2 veritabanından IP → ülke/şehir çözümlemesi yapar.
/// Singleton olarak register edilir (DatabaseReader thread-safe).
/// </summary>
public sealed class MaxMindGeoIpResolver : IGeoIpResolver, IDisposable
{
    private readonly DatabaseReader? _reader;
    private readonly ILogger<MaxMindGeoIpResolver> _logger;

    public MaxMindGeoIpResolver(
        IConfiguration configuration,
        ILogger<MaxMindGeoIpResolver> logger)
    {
        _logger = logger;

        var dbPath = configuration["GeoIp:DatabasePath"];
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
        {
            _logger.LogWarning(
                "GeoIP veritabanı bulunamadı: {Path}. Coğrafi çözümleme devre dışı",
                dbPath ?? "(yapılandırılmamış)");
            return;
        }

        _reader = new DatabaseReader(dbPath);
        _logger.LogInformation("GeoIP veritabanı yüklendi: {Path}", dbPath);
    }

    public (string? CountryCode, string? City) Resolve(IPAddress? ip)
    {
        if (ip is null || _reader is null)
            return (null, null);

        // Loopback ve private IP'ler GeoIP'de çözümlenemez
        if (IPAddress.IsLoopback(ip) || IsPrivate(ip))
            return (null, null);

        try
        {
            if (_reader.TryCity(ip, out var response))
            {
                return (
                    response?.Country.IsoCode,
                    response?.City.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GeoIP çözümlemesi başarısız: {Ip}", ip);
        }

        return (null, null);
    }

    public void Dispose()
    {
        _reader?.Dispose();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] switch
        {
            10 => true,
            172 => bytes[1] >= 16 && bytes[1] <= 31,
            192 => bytes[1] == 168,
            _ => false,
        };
    }
}
