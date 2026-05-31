using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class DailyLimitExceededExceptionHandler(
    ILogger<DailyLimitExceededExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer,
    TimeProvider timeProvider)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not DailyLimitExceededException ex)
            return false;

        logger.LogWarning("Günlük hesaplama/sorgu limiti aşıldı: limit={Limit}", ex.Limit);

        // APIR-038: UTC offset taşıyan DateTimeOffset — istemci ISO 8601 timezone-aware
        // parse edebilir. Önceki sürüm `DateTime.Date.ToString("O")` "2026-05-29T00:00:00.0000000"
        // (Kind=Unspecified) üretiyordu; "Z" ya da "+00:00" suffix yoktu.
        var resetAt = new DateTimeOffset(timeProvider.GetUtcNow().UtcDateTime.Date.AddDays(1), TimeSpan.Zero).ToString("O");

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type    = "https://saydin.app/errors/daily-limit-exceeded",
            Title   = localizer["DailyLimitExceeded"],
            Status  = StatusCodes.Status429TooManyRequests,
            Detail  = string.Format(localizer["DailyLimitExceededDetail"], ex.Limit),
            Extensions =
            {
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ["code"]    = ApiErrorCodes.DailyLimitExceeded,
                ["limit"]   = ex.Limit,
                ["resetAt"] = resetAt
            }
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson, ct);

        return true;
    }
}
