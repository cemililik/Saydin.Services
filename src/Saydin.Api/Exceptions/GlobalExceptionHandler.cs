using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        logger.LogError(
            exception,
            "İşlenmemiş exception: {ExceptionType} — TraceId: {TraceId}",
            exception.GetType().Name,
            traceId);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/internal-error",
            Title = localizer["ServerError"],
            Status = StatusCodes.Status500InternalServerError,
            Detail = localizer["UnexpectedError"],
            Extensions = { ["traceId"] = traceId }
        }, ct);

        return true;
    }
}
