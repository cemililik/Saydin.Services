using System.Diagnostics;
using System.Net.Mime;
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
            Detail = string.Format(localizer["ScenarioLimitExceededDetail"], ex.Limit),
            Extensions =
            {
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ["code"] = ApiErrorCodes.ScenarioLimitExceeded,
                ["limit"] = ex.Limit
            }
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson, ct);

        return true;
    }
}
