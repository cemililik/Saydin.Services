namespace Saydin.Api.Endpoints;

internal static class EndpointExtensions
{
    internal static RouteHandlerBuilder RequireDeviceId(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var deviceId = ctx.HttpContext.Request.Headers["X-Device-ID"].ToString();
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return Results.Problem(
                    title: "X-Device-ID gerekli",
                    detail: "Bu endpoint'e erişmek için X-Device-ID header'ı gönderilmelidir.",
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://saydin.app/errors/missing-device-id");
            }
            return await next(ctx);
        });
}
