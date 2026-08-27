using System.Text.Json;
using Saydin.Api.Services;
using Saydin.Shared.Constants;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.Helpers;

/// <summary>
/// Endpoint handler'larında ActivityLog oluşturmayı kolaylaştıran builder.
/// Stopwatch otomatik olarak başlatılır.
/// </summary>
public sealed class ActivityLogBuilder
{
    // Sonar S1192: "unknown" literal 4 yerde tekrarlıyordu — tek sabite indirgendi.
    // Action whitelist'ine düşmeyen action veya principal pseudonym fallback'i için kullanılır.
    private const string UnknownFallback = "unknown";

    private readonly HttpContext _httpContext;
    private readonly IGeoIpResolver? _geoIpResolver;
    private readonly TimeProvider _timeProvider;
    private readonly long _startedTimestamp;

    private Guid? _userId;
    private string? _action;
    private object? _data;
    private short _statusCode = 200;
    private string? _errorCode;

    public ActivityLogBuilder(
        HttpContext httpContext,
        IGeoIpResolver? geoIpResolver = null,
        TimeProvider? timeProvider = null)
    {
        _httpContext = httpContext;
        _geoIpResolver = geoIpResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedTimestamp = _timeProvider.GetTimestamp();
    }

    public ActivityLogBuilder WithAction(string action)
    {
        // LOGR-002: action whitelist enforcement Build aşamasında — burada yalnız set edilir,
        // doğrulama insertion path'inde (Channel sınırı) yapılır.
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

    public ActivityLogBuilder WithResponseStatus(short statusCode)
    {
        _statusCode = statusCode;
        _errorCode ??= ActivityAuditOutcome.ErrorCode(statusCode);
        return this;
    }

    public ActivityLog Build()
    {
        // Build çağrılmadan önce WithAction zorunludur (ActivityLog.Action NOT NULL).
        // ActivityLogMiddleware Send'i otomatik çağırdığı için sessizce 'unknown' yazmak
        // yerine bug'ı görünür hâle getiriyoruz.
        if (string.IsNullOrWhiteSpace(_action))
            throw new InvalidOperationException(
                "ActivityLogBuilder.Build çağrılmadan önce WithAction(...) ile bir action belirlenmelidir.");

        // The legacy column name is retained in storage, but its value is now only
        // a server-issued principal pseudonym. Client headers never populate it.
        var deviceId = _httpContext.Items[Endpoints.EndpointExtensions.PrincipalActivityIdItemKey] as string
                       ?? UnknownFallback;
        deviceId = TruncateSurrogateSafe(deviceId, ActivityLogLimits.DeviceIdMaxLength) ?? UnknownFallback;

        // Önce orijinal IP'den lokasyon çöz, sonra IP'yi maskele
        var rawIp = _httpContext.Connection.RemoteIpAddress;
        var (country, city) = _geoIpResolver?.Resolve(rawIp) ?? (null, null);

        // LOGR-028: data JSONB CHECK (10000 byte) burada da pre-validate edilir.
        // LOGR-028 follow-up (Codacy uyarısı): önceki sürüm yalnızca local bayrak
        // set ediyordu ve hiçbir observer onu okumuyordu (Build'den sonra builder
        // discard ediliyor). Metric Build() içinde doğrudan artırılır → operasyon
        // ekibi `saydin.activity_log.data.truncations.total` üzerinden görür.
        // Action tag'i whitelist'e tabi (kardinalite kontrolü).
        JsonElement? serializedData = null;
        if (_data is not null)
        {
            var element = JsonSerializer.SerializeToElement(_data);
            var byteSize = JsonbStorageSize.UpperBound(element);
            if (byteSize > ActivityLogLimits.DataMaxBytes)
            {
                var actionTag = ActivityActions.Lookup.Contains(_action!) ? _action! : UnknownFallback;
                SaydinMetrics.ActivityLogDataTruncations.Add(1,
                    new KeyValuePair<string, object?>("action", actionTag));
                // Boş `{"_truncated":true}` placeholder ile yine satır yazılır,
                // observability'de "data was dropped" sinyali kalır.
                serializedData = JsonSerializer.SerializeToElement(new
                {
                    _truncated = true,
                    estimatedJsonbBytes = byteSize,
                });
            }
            else
            {
                serializedData = element;
            }
        }

        return new ActivityLog
        {
            UserId = _userId,
            DeviceId = deviceId,
            // LOGR-002: action whitelist — bilinmeyen action UnknownFallback fallback ile
            // CHECK constraint ihlali engellenir; row CHECK'te düşmez, bisection retry tetiklenmez.
            Action = ActivityActions.Lookup.Contains(_action!) ? _action! : UnknownFallback,
            IpAddress = IpMasker.Mask(rawIp),
            Country = country,
            City = city,
            // F2.1-12 + LOGR-006: DB kolon kapasiteleriyle uyumlu + UTF-16 surrogate-safe truncation.
            DeviceOs   = TruncateSurrogateSafe(_httpContext.Request.Headers["X-Device-OS"].FirstOrDefault(),         ActivityLogLimits.DeviceOsMaxLength),
            OsVersion  = TruncateSurrogateSafe(_httpContext.Request.Headers["X-Device-OS-Version"].FirstOrDefault(), ActivityLogLimits.OsVersionMaxLength),
            AppVersion = TruncateSurrogateSafe(_httpContext.Request.Headers["X-App-Version"].FirstOrDefault(),       ActivityLogLimits.AppVersionMaxLength),
            Data = serializedData,
            StatusCode = _statusCode,
            // F2.1-9 ([C-A-29]): long olarak tut — int cast 24.8 günden uzun sürede
            // overflow yapardı; migration 011 ile DB kolonu BIGINT'e genişler.
            DurationMs = Math.Max(0,
                (long)_timeProvider.GetElapsedTime(_startedTimestamp).TotalMilliseconds),
            ErrorCode  = TruncateSurrogateSafe(_errorCode, ActivityLogLimits.ErrorCodeMaxLength),
            CreatedAt = _timeProvider.GetUtcNow(),
        };
    }

    /// <summary>
    /// F2.1-12 + LOGR-006: DB kolon kapasitesini aşan değerleri kırpar. UTF-16 surrogate
    /// pair'in ortasından kesme yapmaz — emoji içeren header (örn. <c>X-Device-OS:
    /// "Android 14 (📱)"</c>) malformed UTF-16 olarak kaydedilmez. Boş string için null döner.
    /// </summary>
    private static string? TruncateSurrogateSafe(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // F6 follow-up: maxLength ≤ 0 ise value[cut - 1] IndexOutOfRangeException
        // fırlatır. Tüm caller'lar ActivityLogLimits sabitleri kullanıyor (min 2),
        // ama defensive guard latent bug'ı kapatır.
        if (maxLength <= 0) return null;
        if (value.Length <= maxLength) return value;

        // Cut point'in tam üzerinde high-surrogate varsa bir karakter geri.
        var cut = maxLength;
        if (char.IsHighSurrogate(value[cut - 1]))
            cut--;
        return value[..cut];
    }

    /// <summary>
    /// Build + Log kısayolu.
    /// </summary>
    public void Send(IActivityLogger logger)
    {
        logger.Log(Build());
    }
}
