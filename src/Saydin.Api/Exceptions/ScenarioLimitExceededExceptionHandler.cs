using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class ScenarioLimitExceededExceptionHandler(
    ILogger<ScenarioLimitExceededExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not ScenarioLimitExceededException ex)
            return false;

        logger.LogWarning("Senaryo limiti aşıldı: limit={Limit}", ex.Limit);

        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/scenario-limit-exceeded",
            Title = localizer["ScenarioLimitExceeded"],
            Status = StatusCodes.Status422UnprocessableEntity,
            Detail = ex.Message,
            Extensions =
            {
                ["traceId"] = Activity.Current?.TraceId.ToString(),
                ["limit"] = ex.Limit
            }
        }, ct);

        return true;
    }
}
