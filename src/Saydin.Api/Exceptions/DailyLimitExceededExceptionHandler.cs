using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class DailyLimitExceededExceptionHandler(
    ILogger<DailyLimitExceededExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not DailyLimitExceededException ex)
            return false;

        logger.LogWarning("Günlük what-if limiti aşıldı: limit={Limit}", ex.Limit);

        var resetAt = DateTime.UtcNow.Date.AddDays(1).ToString("O");

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type    = "https://saydin.app/errors/daily-limit-exceeded",
            Title   = localizer["DailyLimitExceeded"],
            Status  = StatusCodes.Status429TooManyRequests,
            Detail  = ex.Message,
            Extensions =
            {
                ["traceId"] = Activity.Current?.TraceId.ToString(),
                ["limit"]   = ex.Limit,
                ["resetAt"] = resetAt
            }
        }, ct);

        return true;
    }
}
