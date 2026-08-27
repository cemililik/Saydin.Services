namespace Saydin.Api.Helpers;

internal static class ActivityAuditOutcome
{
    public static string? ErrorCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "request_invalid",
        StatusCodes.Status401Unauthorized => "authentication_failed",
        StatusCodes.Status403Forbidden => "request_forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status422UnprocessableEntity => "unprocessable_entity",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        StatusCodes.Status502BadGateway => "bad_gateway",
        StatusCodes.Status503ServiceUnavailable => "service_unavailable",
        >= 500 => "internal_error",
        _ => null,
    };
}
