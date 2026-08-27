using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Saydin.Api.Exceptions;

namespace Saydin.Api.Security;

internal static class SecurityAdmissionProblem
{
    internal static IResult Result(
        HttpContext context,
        IStringLocalizer<ErrorMessages> localizer,
        SecurityLimiterDecision decision)
    {
        if (decision.Outcome == SecurityLimiterOutcome.Limited)
        {
            SetRetryAfter(context.Response, decision.RetryAfter);
            return Results.Problem(
                title: localizer["RateLimited"],
                detail: localizer["SecurityRateLimitedDetail"],
                statusCode: StatusCodes.Status429TooManyRequests,
                type: "https://saydin.app/errors/security-rate-limited",
                extensions: Extensions(context, ApiErrorCodes.SecurityRateLimited));
        }

        if (decision.Outcome != SecurityLimiterOutcome.Unavailable)
            throw new ArgumentOutOfRangeException(nameof(decision));

        var addressUntrusted = decision.Reason == SecurityLimiterReason.InvalidSubject;
        if (!addressUntrusted)
            SetRetryAfter(context.Response, decision.RetryAfter);
        return Results.Problem(
            title: localizer[addressUntrusted
                ? "SecurityClientAddressUntrusted" : "SecurityLimiterUnavailable"],
            detail: localizer[addressUntrusted
                ? "SecurityClientAddressUntrustedDetail" : "SecurityLimiterUnavailableDetail"],
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: addressUntrusted
                ? "https://saydin.app/errors/security-client-address-untrusted"
                : "https://saydin.app/errors/security-limiter-unavailable",
            extensions: Extensions(context, addressUntrusted
                ? ApiErrorCodes.SecurityClientAddressUntrusted
                : ApiErrorCodes.SecurityLimiterUnavailable));
    }

    internal static async Task WriteAsync(
        HttpContext context,
        IStringLocalizer<ErrorMessages> localizer,
        SecurityLimiterDecision decision)
    {
        if (decision.Outcome == SecurityLimiterOutcome.Limited ||
            decision.Outcome == SecurityLimiterOutcome.Unavailable &&
            decision.Reason != SecurityLimiterReason.InvalidSubject)
            SetRetryAfter(context.Response, decision.RetryAfter);
        var limited = decision.Outcome == SecurityLimiterOutcome.Limited;
        var addressUntrusted = decision.Reason == SecurityLimiterReason.InvalidSubject;
        var status = limited
            ? StatusCodes.Status429TooManyRequests
            : StatusCodes.Status503ServiceUnavailable;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = limited
                ? "https://saydin.app/errors/security-rate-limited"
                : addressUntrusted
                    ? "https://saydin.app/errors/security-client-address-untrusted"
                    : "https://saydin.app/errors/security-limiter-unavailable",
            Title = limited
                ? localizer["RateLimited"]
                : localizer[addressUntrusted
                    ? "SecurityClientAddressUntrusted" : "SecurityLimiterUnavailable"],
            Detail = limited
                ? localizer["SecurityRateLimitedDetail"]
                : localizer[addressUntrusted
                    ? "SecurityClientAddressUntrustedDetail" : "SecurityLimiterUnavailableDetail"],
            Status = status,
            Extensions =
            {
                ["code"] = limited
                    ? ApiErrorCodes.SecurityRateLimited
                    : addressUntrusted
                        ? ApiErrorCodes.SecurityClientAddressUntrusted
                        : ApiErrorCodes.SecurityLimiterUnavailable,
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            },
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson,
            context.RequestAborted);
    }

    private static Dictionary<string, object?> Extensions(HttpContext context, string code) => new()
    {
        ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
        ["code"] = code,
    };

    private static void SetRetryAfter(HttpResponse response, TimeSpan retryAfter)
    {
        var retrySeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        response.Headers.RetryAfter = retrySeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
