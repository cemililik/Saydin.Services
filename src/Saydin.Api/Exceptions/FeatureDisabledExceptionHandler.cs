using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Saydin.Shared.Exceptions;

namespace Saydin.Api.Exceptions;

public sealed class FeatureDisabledExceptionHandler(
    ILogger<FeatureDisabledExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not FeatureDisabledException ex)
            return false;

        logger.LogInformation(
            "Tier feature devre dışı: {Feature}", ex.FeatureKey ?? "(unknown)");

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;

        // Activity.Current null olabilir; ProblemDetails her zaman traceId taşımalı.
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = traceId,
            ["code"] = ApiErrorCodes.FeatureDisabled,
        };
        if (ex.FeatureKey is not null)
            extensions["feature"] = ex.FeatureKey;

        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type   = "https://saydin.app/errors/feature-disabled",
            Title  = localizer["FeatureDisabledTitle"],
            Status = StatusCodes.Status403Forbidden,
            Detail = ex.Detail,
            Extensions = extensions,
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson, cancellationToken);

        return true;
    }
}
