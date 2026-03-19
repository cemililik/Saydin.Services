using Microsoft.Extensions.Localization;

namespace Saydin.Api.Endpoints;

internal static class EndpointExtensions
{
    internal const string DeviceIdItemKey = "DeviceId";

    private const string DeviceIdHeader   = "X-Device-ID";
    private const int    MaxDeviceIdLength = 128;

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

            if (deviceId.Length > MaxDeviceIdLength ||
                !deviceId.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
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
