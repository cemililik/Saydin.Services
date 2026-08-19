namespace Saydin.Api.Helpers;

internal static class ActivityAuditOutcome
{
    public static string? ErrorCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "request_invalid",
        StatusCodes.Status401Unauthorized => "authentication_failed",
        StatusCodes.Status403Forbidden => "request_forbidden",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        StatusCodes.Status503ServiceUnavailable => "service_unavailable",
        >= 500 => "internal_error",
        _ => null,
    };
}
