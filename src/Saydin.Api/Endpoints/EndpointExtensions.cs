namespace Saydin.Api.Endpoints;

internal static class EndpointExtensions
{
    internal const string DeviceIdItemKey = "DeviceId";

    private const string DeviceIdHeader   = "X-Device-ID";
    private const int    MaxDeviceIdLength = 128;

    internal static RouteHandlerBuilder RequireDeviceId(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var headerValues = ctx.HttpContext.Request.Headers[DeviceIdHeader];

            if (headerValues.Count != 1 || string.IsNullOrWhiteSpace(headerValues[0]))
            {
                return Results.Problem(
                    title: "X-Device-ID gerekli",
                    detail: "Bu endpoint'e erişmek için X-Device-ID header'ı tek, boş olmayan bir değerle gönderilmelidir.",
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://saydin.app/errors/missing-device-id");
            }

            var deviceId = headerValues[0]!.Trim();

            if (deviceId.Length > MaxDeviceIdLength ||
                !deviceId.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return Results.Problem(
                    title: "X-Device-ID geçersiz",
                    detail: $"X-Device-ID en fazla {MaxDeviceIdLength} karakter olmalı ve yalnızca harf, rakam, '-', '_' ve '.' içerebilir.",
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://saydin.app/errors/invalid-device-id");
            }

            ctx.HttpContext.Items[DeviceIdItemKey] = deviceId;
            return await next(ctx);
        });
}
