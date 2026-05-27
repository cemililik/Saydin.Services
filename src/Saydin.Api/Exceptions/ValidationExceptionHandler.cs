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
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not ValidationException ex)
            return false;

        logger.LogInformation(
            "Geçersiz istek alanı: {Field} — {Detail}",
            ex.Field ?? "(none)", ex.Detail);

        context.Response.StatusCode = StatusCodes.Status400BadRequest;

        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = Activity.Current?.TraceId.ToString()
        };
        if (ex.Field is not null)
            extensions["field"] = ex.Field;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type   = "https://saydin.app/errors/validation",
            Title  = localizer["ValidationFailed"],
            Status = StatusCodes.Status400BadRequest,
            Detail = ex.Detail,
            Extensions = extensions,
        }, ct);

        return true;
    }
}
