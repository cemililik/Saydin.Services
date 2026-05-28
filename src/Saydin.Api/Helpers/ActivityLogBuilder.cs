using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Saydin.Api.Services;
using Saydin.Shared.Entities;

namespace Saydin.Api.Helpers;

/// <summary>
/// Endpoint handler'larında ActivityLog oluşturmayı kolaylaştıran builder.
/// Stopwatch otomatik olarak başlatılır.
/// </summary>
public sealed class ActivityLogBuilder
{
    // F2.1-12 ([G-A-02]) / DB kapasiteleri ile uyumlu header truncation limitleri.
    // activity_logs şemasındaki kolon kapasiteleriyle birebir aynı olmalı (009 migration).
    private const int MaxDeviceOsLength   = 30;
    private const int MaxOsVersionLength  = 100;
    private const int MaxAppVersionLength = 50;
    private const int MaxErrorCodeLength  = 50;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly HttpContext _httpContext;
    private readonly IGeoIpResolver? _geoIpResolver;

    private Guid? _userId;
    private string? _action;
    private object? _data;
    private short _statusCode = 200;
    private string? _errorCode;

    public ActivityLogBuilder(HttpContext httpContext, IGeoIpResolver? geoIpResolver = null)
    {
        _httpContext = httpContext;
        _geoIpResolver = geoIpResolver;
    }

    public ActivityLogBuilder WithAction(string action)
    {
        _action = action;
        return this;
    }

    public ActivityLogBuilder WithUserId(Guid? userId)
    {
        _userId = userId;
        return this;
    }

    public ActivityLogBuilder WithData(object data)
    {
        _data = data;
        return this;
    }

    public ActivityLogBuilder WithStatusCode(short statusCode)
    {
        _statusCode = statusCode;
        return this;
    }

    public ActivityLogBuilder WithError(short statusCode, string errorCode)
    {
        _statusCode = statusCode;
        _errorCode = errorCode;
        return this;
    }

    public ActivityLog Build()
    {
        _stopwatch.Stop();

        // Build çağrılmadan önce WithAction zorunludur (ActivityLog.Action NOT NULL).
        // ActivityLogMiddleware Send'i otomatik çağırdığı için sessizce 'unknown' yazmak
        // yerine bug'ı görünür hâle getiriyoruz.
        if (string.IsNullOrWhiteSpace(_action))
            throw new InvalidOperationException(
                "ActivityLogBuilder.Build çağrılmadan önce WithAction(...) ile bir action belirlenmelidir.");

        var deviceId = _httpContext.Items[Endpoints.EndpointExtensions.DeviceIdItemKey] as string
                       ?? _httpContext.Request.Headers["X-Device-ID"].FirstOrDefault()
                       ?? "unknown";

        // Önce orijinal IP'den lokasyon çöz, sonra IP'yi maskele
        var rawIp = _httpContext.Connection.RemoteIpAddress;
        var (country, city) = _geoIpResolver?.Resolve(rawIp) ?? (null, null);

        return new ActivityLog
        {
            UserId = _userId,
            DeviceId = deviceId,
            Action = _action!,
            IpAddress = IpMasker.Mask(rawIp),
            Country = country,
            City = city,
            // F2.1-12: DB kolon kapasiteleriyle uyumlu truncation.
            DeviceOs   = Truncate(_httpContext.Request.Headers["X-Device-OS"].FirstOrDefault(),         MaxDeviceOsLength),
            OsVersion  = Truncate(_httpContext.Request.Headers["X-Device-OS-Version"].FirstOrDefault(), MaxOsVersionLength),
            AppVersion = Truncate(_httpContext.Request.Headers["X-App-Version"].FirstOrDefault(),       MaxAppVersionLength),
            Data = _data is not null
                ? JsonSerializer.SerializeToElement(_data)
                : null,
            StatusCode = _statusCode,
            // F2.1-9 ([C-A-29]): long olarak tut — int cast 24.8 günden uzun sürede
            // overflow yapardı; migration 011 ile DB kolonu BIGINT'e genişler.
            DurationMs = _stopwatch.ElapsedMilliseconds,
            ErrorCode  = Truncate(_errorCode, MaxErrorCodeLength),
        };
    }

    /// <summary>
    /// F2.1-12: DB kolon kapasitesini aşan header değerlerini sessizce kırpar.
    /// Activity log telemetri amaçlıdır; ham header değerinin tamamını saklamak
    /// kritik değil ama row yazımının fail olması toplam observability'yi düşürür.
    /// </summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    /// <summary>
    /// Build + Log kısayolu.
    /// </summary>
    public void Send(IActivityLogger logger)
    {
        logger.Log(Build());
    }
}
