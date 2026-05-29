using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;

namespace Saydin.Api.Endpoints;

internal static partial class EndpointExtensions
{
    internal const string DeviceIdItemKey = "DeviceId";

    private const string DeviceIdHeader   = "X-Device-ID";
    private const int    MaxDeviceIdLength = 128;

    /// <summary>
    /// F2.1-3 ([C-A-8]): ASCII-only sınıf — <see cref="char.IsLetterOrDigit"/>
    /// Unicode harf ve rakamları da kabul eder (örn. <c>İ</c>, <c>ǅ</c>).
    /// DeviceId Flutter SecureStorage UUID'leri için ASCII alphanumeric + ayraç
    /// karakterleri (<c>-_.</c>) ile sınırlı.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex DeviceIdPattern();

    /// <summary>
    /// <c>RequireDeviceId()</c> filter'ı <see cref="DeviceIdItemKey"/> öğesini set ettiği için
    /// burada normalde null dönmez. Filter atlanırsa InvalidOperationException → 500;
    /// bu noktayı görünür kılmak için <see cref="GlobalExceptionHandler"/> tarafından
    /// loglanır. (Tüm endpoint'lerde tekrarlanan boilerplate'i bu helper'a topladık.)
    /// </summary>
    internal static string GetRequiredDeviceId(this HttpContext context) =>
        context.Items[DeviceIdItemKey] as string
            ?? throw new InvalidOperationException(
                "DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");

    internal static RouteHandlerBuilder RequireDeviceId(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var localizer = ctx.HttpContext.RequestServices
                .GetRequiredService<IStringLocalizer<ErrorMessages>>();

            var headerValues = ctx.HttpContext.Request.Headers[DeviceIdHeader];

            if (headerValues.Count != 1 || string.IsNullOrWhiteSpace(headerValues[0]))
            {
                return Results.Problem(
                    title: localizer["DeviceIdRequired"],
                    detail: localizer["DeviceIdRequiredDetail"],
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://saydin.app/errors/missing-device-id");
            }

            var deviceId = headerValues[0]!.Trim();

            if (deviceId.Length > MaxDeviceIdLength || !DeviceIdPattern().IsMatch(deviceId))
            {
                return Results.Problem(
                    title: localizer["DeviceIdInvalid"],
                    detail: string.Format(localizer["DeviceIdInvalidDetail"], MaxDeviceIdLength),
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://saydin.app/errors/invalid-device-id");
            }

            ctx.HttpContext.Items[DeviceIdItemKey] = deviceId;
            return await next(ctx);
        });
}
