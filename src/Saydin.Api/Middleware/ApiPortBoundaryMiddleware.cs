using Saydin.Api.Runtime;

namespace Saydin.Api.Middleware;

public sealed class ApiPortBoundaryMiddleware(
    ApiRuntimeContract runtime,
    IHostEnvironment environment) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var kind = Classify(context, runtime, environment);
        if (kind == ApiPortRequestKind.Rejected)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
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
        var path = context.Request.Path;

        if (path == ApiPortBoundary.LivePath)
            return isPublicPort ? ApiPortRequestKind.PublicLiveness : ApiPortRequestKind.Rejected;
        if (path == ApiPortBoundary.ReadyPath || path == ApiPortBoundary.MetricsPath)
            return isManagementPort ? ApiPortRequestKind.Management : ApiPortRequestKind.Rejected;
        return isPublicPort ? ApiPortRequestKind.PublicProduct : ApiPortRequestKind.Rejected;
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
