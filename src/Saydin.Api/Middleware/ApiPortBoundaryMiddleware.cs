using Saydin.Api.Runtime;
using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Saydin.Api.Exceptions;

namespace Saydin.Api.Middleware;

public sealed class ApiPortBoundaryMiddleware(
    ApiRuntimeContract runtime,
    IHostEnvironment environment,
    IStringLocalizer<ErrorMessages> localizer) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var kind = Classify(context, runtime, environment);
        if (kind == ApiPortRequestKind.Rejected)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Type = "https://saydin.app/errors/route-not-found",
                Title = localizer["RouteNotFound"],
                Detail = localizer["RouteNotFoundDetail"],
                Status = StatusCodes.Status404NotFound,
                Extensions =
                {
                    ["code"] = ApiErrorCodes.RouteNotFound,
                    ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                },
            }, options: null, contentType: MediaTypeNames.Application.ProblemJson,
                context.RequestAborted);
            return;
        }
        context.Items[ApiPortBoundary.RequestKindItemKey] = kind;
        await next(context);
    }

    internal static ApiPortRequestKind Classify(
        HttpContext context,
        ApiRuntimeContract runtime,
        IHostEnvironment environment)
    {
        var port = context.Connection.LocalPort;
        var testServerPublic = port == 0 && !environment.IsProduction();
        var isPublicPort = port == runtime.PublicPort || testServerPublic;
        var isManagementPort = port == runtime.ManagementPort;
        var path = NormalizePath(context.Request.Path.Value);

        if (string.Equals(path, ApiPortBoundary.LivePath, StringComparison.OrdinalIgnoreCase))
            return isPublicPort ? ApiPortRequestKind.PublicLiveness : ApiPortRequestKind.Rejected;
        if (string.Equals(path, ApiPortBoundary.ReadyPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, ApiPortBoundary.MetricsPath, StringComparison.OrdinalIgnoreCase))
            return isManagementPort ? ApiPortRequestKind.Management : ApiPortRequestKind.Rejected;
        return isPublicPort ? ApiPortRequestKind.PublicProduct : ApiPortRequestKind.Rejected;
    }

    internal static string NormalizePath(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "/";
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }
}

public enum ApiPortRequestKind
{
    Rejected,
    PublicProduct,
    PublicLiveness,
    Management,
}

public static class ApiPortBoundary
{
    public const string RequestKindItemKey = "__saydin-api-port-kind";
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string MetricsPath = "/metrics";

    public static bool IsAdmissionExempt(HttpContext context) =>
        context.Items.TryGetValue(RequestKindItemKey, out var value)
        && value is ApiPortRequestKind.PublicLiveness or ApiPortRequestKind.Management;
}
