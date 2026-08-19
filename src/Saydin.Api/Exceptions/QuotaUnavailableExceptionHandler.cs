using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Saydin.Api.Services;

namespace Saydin.Api.Exceptions;

public sealed class QuotaUnavailableExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not QuotaUnavailableException) return false;

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/quota-unavailable",
            Title = "Quota service unavailable.",
            Status = StatusCodes.Status503ServiceUnavailable,
            Extensions =
            {
                ["code"] = QuotaUnavailableException.ErrorCode,
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            },
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson,
            cancellationToken);
        return true;
    }
}
