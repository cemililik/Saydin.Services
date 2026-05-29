using System.Diagnostics;
using System.Text;
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
    // Action whitelist'ine düşmeyen action veya DeviceId fallback'i için kullanılır.
    private const string UnknownFallback = "unknown";

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

    public ActivityLog Build()
    {
        _stopwatch.Stop();

        // Build çağrılmadan önce WithAction zorunludur (ActivityLog.Action NOT NULL).
        // ActivityLogMiddleware Send'i otomatik çağırdığı için sessizce 'unknown' yazmak
        // yerine bug'ı görünür hâle getiriyoruz.
        if (string.IsNullOrWhiteSpace(_action))
            throw new InvalidOperationException(
                "ActivityLogBuilder.Build çağrılmadan önce WithAction(...) ile bir action belirlenmelidir.");

        // LOGR-008: DeviceId truncation — 200 char üstü payload tüm batch'i drop
        // edebilirdi (CHECK + bisection retry maliyeti). Pre-validation burada.
        var deviceId = _httpContext.Items[Endpoints.EndpointExtensions.DeviceIdItemKey] as string
                       ?? _httpContext.Request.Headers["X-Device-ID"].FirstOrDefault()
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
            var byteSize = EstimateUtf8Size(element);
            if (byteSize > ActivityLogLimits.DataMaxBytes)
            {
                var actionTag = ActivityActions.Lookup.Contains(_action!) ? _action! : UnknownFallback;
                SaydinMetrics.ActivityLogDataTruncations.Add(1,
                    new KeyValuePair<string, object?>("action", actionTag));
                // Boş `{"_truncated":true}` placeholder ile yine satır yazılır,
                // observability'de "data was dropped" sinyali kalır.
                serializedData = JsonSerializer.SerializeToElement(new { _truncated = true, originalBytes = byteSize });
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
            DurationMs = _stopwatch.ElapsedMilliseconds,
            ErrorCode  = TruncateSurrogateSafe(_errorCode, ActivityLogLimits.ErrorCodeMaxLength),
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
    /// LOGR-028: JsonElement'in UTF-8 serileştirme boyutu yaklaşık tahmini.
    /// `pg_column_size` ile birebir aynı değil ama %95+ doğru — pre-validation
    /// için makul. Tam ölçüm için JsonSerializer.SerializeToUtf8Bytes kullanılır
    /// (allocation) — yaklaşım yeterli.
    /// </summary>
    private static int EstimateUtf8Size(JsonElement element)
    {
        // RawText kullan: JSON binary olarak kompakt değil ama overhead %5 civarında.
        var text = element.GetRawText();
        return Encoding.UTF8.GetByteCount(text);
    }

    /// <summary>
    /// Build + Log kısayolu.
    /// </summary>
    public void Send(IActivityLogger logger)
    {
        logger.Log(Build());
    }
}
