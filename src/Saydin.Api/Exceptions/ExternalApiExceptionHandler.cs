using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

/// <summary>
/// Dış API çağrılarından gelen <see cref="ExternalApiException"/>'ı 502 Bad Gateway
/// olarak kullanıcıya yansıtır (review F1.2-6 / [G-E-04]).
/// Saydin.Api kendi adapter'larını çalıştırmasa da, gelecekte downstream-bağımlı
/// senaryolar için hazır olur; Saydin.PriceIngestion'dan domain'e sızabilecek
/// exception tipleri de aynı zincire düşer.
/// </summary>
public sealed class ExternalApiExceptionHandler(
    ILogger<ExternalApiExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not ExternalApiException ex)
            return false;

        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        logger.LogWarning(
            ex,
            "Dış API hatası → 502: {Source} — TraceId: {TraceId}",
            ex.ApiSource,
            traceId);

        context.Response.StatusCode = StatusCodes.Status502BadGateway;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://saydin.app/errors/external-api",
            Title = localizer["ServerError"],
            Status = StatusCodes.Status502BadGateway,
            Detail = localizer["UnexpectedError"],
            Extensions =
            {
                ["traceId"] = traceId,
                ["source"]  = ex.ApiSource,
            }
        }, ct);

        return true;
    }
}
