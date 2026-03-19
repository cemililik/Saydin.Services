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
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly HttpContext _httpContext;
    private readonly IGeoIpResolver? _geoIpResolver;

    private Guid? _userId;
    private string _action = default!;
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
            Action = _action,
            IpAddress = IpMasker.Mask(rawIp),
            Country = country,
            City = city,
            DeviceOs = _httpContext.Request.Headers["X-Device-OS"].FirstOrDefault(),
            OsVersion = _httpContext.Request.Headers["X-Device-OS-Version"].FirstOrDefault(),
            AppVersion = _httpContext.Request.Headers["X-App-Version"].FirstOrDefault(),
            Data = _data is not null
                ? JsonSerializer.SerializeToElement(_data)
                : null,
            StatusCode = _statusCode,
            DurationMs = (int)_stopwatch.ElapsedMilliseconds,
            ErrorCode = _errorCode,
        };
    }

    /// <summary>
    /// Build + Log kısayolu.
    /// </summary>
    public void Send(IActivityLogger logger)
    {
        logger.Log(Build());
    }
}
