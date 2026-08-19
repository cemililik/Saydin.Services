using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Saydin.Api.Exceptions;

public sealed class RequestBodyTooLargeExceptionHandler(
    ILogger<RequestBodyTooLargeExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not RequestBodyTooLargeException ex)
            return false;

        logger.LogWarning("İstek gövdesi endpoint limitini aştı: maxBytes={MaxBytes}", ex.MaxBytes);
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/payload-too-large",
            Title = localizer["PayloadTooLarge"],
            Status = StatusCodes.Status413PayloadTooLarge,
            Detail = string.Format(localizer["PayloadTooLargeDetail"], ex.MaxBytes),
            Extensions =
            {
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ["code"] = ApiErrorCodes.PayloadTooLarge,
                ["maxBytes"] = ex.MaxBytes,
            }
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson, ct);

        return true;
    }
}
