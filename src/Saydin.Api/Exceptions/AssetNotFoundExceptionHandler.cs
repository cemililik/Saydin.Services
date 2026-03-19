using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class AssetNotFoundExceptionHandler(
    ILogger<AssetNotFoundExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not AssetNotFoundException ex)
            return false;

        logger.LogWarning("Varlık bulunamadı: {Symbol}", ex.Symbol);

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/asset-not-found",
            Title = localizer["AssetNotFound"],
            Status = StatusCodes.Status404NotFound,
            Detail = ex.Message,
            Extensions = { ["traceId"] = Activity.Current?.TraceId.ToString() }
        }, ct);

        return true;
    }
}
