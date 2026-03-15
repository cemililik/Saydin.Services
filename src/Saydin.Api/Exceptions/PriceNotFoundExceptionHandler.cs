using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class PriceNotFoundExceptionHandler(ILogger<PriceNotFoundExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not PriceNotFoundException ex)
            return false;

        logger.LogWarning(
            "Fiyat bulunamadı: {Symbol} / {Date}",
            ex.AssetSymbol,
            ex.Date);

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/price-not-found",
            Title = "Fiyat bulunamadı",
            Status = StatusCodes.Status404NotFound,
            Detail = ex.Message,
            Extensions =
            {
                ["traceId"] = Activity.Current?.TraceId.ToString(),
                ["nearestDates"] = ex.NearestAvailableDates
            }
        }, ct);

        return true;
    }
}
