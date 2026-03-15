using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class ScenarioNotFoundExceptionHandler(ILogger<ScenarioNotFoundExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not ScenarioNotFoundException ex)
            return false;

        logger.LogWarning("Senaryo bulunamadı: {ScenarioId}", ex.ScenarioId);

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/scenario-not-found",
            Title = "Senaryo bulunamadı",
            Status = StatusCodes.Status404NotFound,
            Detail = ex.Message,
            Extensions = { ["traceId"] = Activity.Current?.TraceId.ToString() }
        }, ct);

        return true;
    }
}
