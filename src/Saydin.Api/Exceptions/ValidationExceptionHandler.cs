using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class ValidationExceptionHandler(
    ILogger<ValidationExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // F1.2-6: Domain `ValidationException` ek olarak `ArgumentException` da yakalanır
        // (örn. `ArgumentException.ThrowIfNullOrWhiteSpace(deviceId)` — istek boyunca asla
        // ulaşılmaması gereken guard'lar). Bu sayede 500 yerine 400 döner ve mesaj
        // kullanıcıya teknik bilgi sızdırmadan lokalize edilir.
        string? detailMessage;
        string? field;

        switch (exception)
        {
            case ValidationException ex:
                detailMessage = ex.Detail;
                field = ex.Field;
                logger.LogInformation(
                    "Geçersiz istek alanı: {Field} — {Detail}",
                    field ?? "(none)", detailMessage);
                break;

            case ArgumentException argEx:
                // ArgumentException.ParamName teknik bir field adıdır; logged'da paylaşılır
                // ama response'da gösterilmez (kullanıcıya internal field isimlerini sızdırma).
                field = null;
                detailMessage = localizer["ValidationFailed"];
                logger.LogWarning(
                    argEx,
                    "ArgumentException → 400 ValidationFailed (ParamName: {ParamName})",
                    argEx.ParamName ?? "(none)");
                break;

            default:
                return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        // Activity.Current null olabilir; ProblemDetails her zaman traceId taşımalı.
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = traceId
        };
        if (field is not null)
            extensions["field"] = field;

        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type   = "https://saydin.app/errors/validation",
            Title  = localizer["ValidationFailed"],
            Status = StatusCodes.Status400BadRequest,
            Detail = detailMessage,
            Extensions = extensions,
        }, cancellationToken);

        return true;
    }
}
