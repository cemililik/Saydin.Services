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
        // F1.2-6 / P1R-003: Yalnızca domain `ValidationException` handle edilir.
        // Önceki sürümde `ArgumentException` da yakalanıyordu, ancak base tip
        // framework ve altyapı katmanlarından (Redis/EF Core/Npgsql) da fırlatılır;
        // bu hatalar 400 olarak yansıtıldığında kök sebep 5xx alarm/dashboard'lardan
        // gizlenir. Servis katmanı artık deviceId/null/whitespace guard'larını
        // doğrudan `ValidationException` ile yapıyor — handler de yalnızca domain
        // exception'ı işliyor, jenerik `ArgumentException` GlobalExceptionHandler'a
        // bırakılıp 500 üretiyor.
        if (exception is not ValidationException ex)
            return false;

        var detailMessage = ex.Detail;
        var field = ex.Field;
        logger.LogInformation(
            "Geçersiz istek alanı: {Field} — {Detail}",
            field ?? "(none)", detailMessage);

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
