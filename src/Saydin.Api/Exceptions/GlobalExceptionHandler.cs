using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
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
            Title = "Sunucu hatası",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.",
            Extensions = { ["traceId"] = traceId }
        }, ct);

        return true;
    }
}
