using System.Diagnostics;
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
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        if (exception is not FeatureDisabledException ex)
            return false;

        logger.LogInformation(
            "Tier feature devre dışı: {Feature}", ex.FeatureKey ?? "(unknown)");

        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = Activity.Current?.TraceId.ToString()
        };
        if (ex.FeatureKey is not null)
            extensions["feature"] = ex.FeatureKey;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type   = "https://saydin.app/errors/feature-disabled",
            Title  = localizer["FeatureDisabledTitle"],
            Status = StatusCodes.Status403Forbidden,
            Detail = ex.Detail,
            Extensions = extensions,
        }, ct);

        return true;
    }
}
