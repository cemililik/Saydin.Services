using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Saydin.Api.Security;

public sealed class DistributedSecurityLimiterMiddleware(
    IDistributedSecurityLimiter limiter,
    IOptions<DistributedSecurityLimiterOptions> options,
    ILogger<DistributedSecurityLimiterMiddleware> logger)
    : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!options.Value.Enabled)
        {
            await next(context);
            return;
        }

        // This middleware must run after UseForwardedHeaders. A trusted, valid
        // X-Forwarded-For value is consumed there. A remaining value is therefore
        // untrusted/malformed (or proves unsafe ordering) and must fail closed.
        var hasUnconsumedForwardedFor =
            !string.IsNullOrWhiteSpace(context.Request.Headers["X-Forwarded-For"].ToString());
        var address = Normalize(context.Connection.RemoteIpAddress);
        if (address is null || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) ||
            hasUnconsumedForwardedFor)
        {
            await WriteUnavailableAsync(context);
            return;
        }

        var decision = await limiter.TryAcquireNetworkAsync(address, context.RequestAborted);
        if (decision.Outcome == SecurityLimiterOutcome.Allowed)
        {
            await next(context);
            return;
        }

        if (decision.Outcome == SecurityLimiterOutcome.Limited)
        {
            var retrySeconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
            context.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
            await WriteProblemAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                "security_rate_limited",
                "https://saydin.app/errors/security-rate-limited",
                "Too many requests.");
            return;
        }

        await WriteUnavailableAsync(context);
    }

    private async Task WriteUnavailableAsync(HttpContext context)
    {
        // Stable-only telemetry: never attach the address, principal, Redis key or exception.
        logger.LogWarning("Distributed security limiter unavailable: {Code}",
            "security_limiter_unavailable");
        await WriteProblemAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "security_limiter_unavailable",
            "https://saydin.app/errors/security-limiter-unavailable",
            "Request admission is temporarily unavailable.");
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string code,
        string type,
        string title)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = status,
            Extensions =
            {
                ["code"] = code,
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            },
        }, options: null, contentType: MediaTypeNames.Application.ProblemJson,
            context.RequestAborted);
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
}
