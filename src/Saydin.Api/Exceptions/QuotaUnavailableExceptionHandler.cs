using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Saydin.Api.Services;

namespace Saydin.Api.Exceptions;

public sealed class QuotaUnavailableExceptionHandler(
    IStringLocalizer<ErrorMessages> localizer) : IExceptionHandler
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
            Title = localizer["QuotaUnavailable"],
            Detail = localizer["QuotaUnavailableDetail"],
            Status = StatusCodes.Status503ServiceUnavailable,
            Extensions =
            {
                ["code"] = ApiErrorCodes.QuotaUnavailable,
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            },
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson,
            cancellationToken);
        return true;
    }
}
