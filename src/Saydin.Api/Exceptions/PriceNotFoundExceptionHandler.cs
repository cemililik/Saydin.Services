using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;
using Saydin.Shared.Diagnostics;

namespace Saydin.Api.Exceptions;

public sealed class PriceNotFoundExceptionHandler(
    ILogger<PriceNotFoundExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer)
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

        // Count at the HTTP exception boundary so a single failed request is never
        // double-counted by nested calculator/service layers.
        SaydinMetrics.PriceNotFoundCount.Add(1);

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/price-not-found",
            Title = localizer["PriceNotFound"],
            Status = StatusCodes.Status404NotFound,
            Detail = string.Format(localizer["PriceNotFoundDetail"],
                ex.Date.ToString("yyyy-MM-dd"), ex.AssetSymbol),
            Extensions =
            {
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ["code"] = ApiErrorCodes.PriceNotFound,
                ["nearestDates"] = ex.NearestAvailableDates
            }
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson, ct);

        return true;
    }
}
