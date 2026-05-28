using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class ValidationExceptionHandler(
    ILogger<ValidationExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException ex)
            return false;

        logger.LogInformation(
            "Geçersiz istek alanı: {Field} — {Detail}",
            ex.Field ?? "(none)", ex.Detail);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        // Activity.Current null olabilir; ProblemDetails her zaman traceId taşımalı.
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = traceId
        };
        if (ex.Field is not null)
            extensions["field"] = ex.Field;

        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type   = "https://saydin.app/errors/validation",
            Title  = localizer["ValidationFailed"],
            Status = StatusCodes.Status400BadRequest,
            Detail = ex.Detail,
            Extensions = extensions,
        }, cancellationToken);

        return true;
    }
}
