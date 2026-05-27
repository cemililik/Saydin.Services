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
    private readonly IHostEnvironment _environment;

    public MaxMindGeoIpResolver(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<MaxMindGeoIpResolver> logger)
    {
        _logger = logger;
        _environment = environment;

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
            // Bilinen edge case (cgnat / kayıp ülke kaydı) production'da gürültü yaratmasın.
            // Detaylı debug yalnızca Development'ta; prod'da Information seviyesi yeterli.
            if (_environment.IsDevelopment())
                _logger.LogDebug(ex, "GeoIP çözümlemesi başarısız: {Ip}", ip);
            else
                _logger.LogInformation("GeoIP çözümlemesi başarısız: {Ip} ({ErrorType})",
                    ip, ex.GetType().Name);
        }

        return (null, null);
    }

    public void Dispose()
    {
        _reader?.Dispose();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        // IPv6 link-local + unique local (fc00::/7) — production'da yaygındır (Docker, K8s).
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
                return true;

            var v6 = ip.GetAddressBytes();
            // fc00::/7 — ULA (0xfc, 0xfd)
            if ((v6[0] & 0xfe) == 0xfc)
                return true;

            return false;
        }

        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] switch
        {
            10  => true,
            // 100.64.0.0/10 — CGNAT (Türk mobil operatörlerinde yaygın)
            100 => bytes[1] >= 64 && bytes[1] <= 127,
            172 => bytes[1] >= 16 && bytes[1] <= 31,
            192 => bytes[1] == 168,
            _   => false,
        };
    }
}
