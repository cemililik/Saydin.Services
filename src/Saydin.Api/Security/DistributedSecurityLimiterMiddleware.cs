using System.Net;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Saydin.Api.Security;

public sealed class DistributedSecurityLimiterMiddleware(
    IDistributedSecurityLimiter limiter,
    IOptions<DistributedSecurityLimiterOptions> options,
    ILogger<DistributedSecurityLimiterMiddleware> logger,
    IStringLocalizer<ErrorMessages> localizer)
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
        if (!TryGetTrustedClientAddress(context, out var address))
        {
            SecurityAdmissionTelemetry.Record(
                SecurityAdmissionTelemetry.NetworkBucket,
                "unavailable",
                SecurityAdmissionTelemetry.ClientAddressUntrustedReason);
            logger.LogWarning("Security admission rejected: {Code}",
                "security_client_address_untrusted");
            await SecurityAdmissionProblem.WriteAsync(
                context, localizer,
                SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.InvalidSubject));
            return;
        }

        var decision = await limiter.TryAcquireNetworkAsync(address, context.RequestAborted);
        SecurityAdmissionTelemetry.Record(SecurityAdmissionTelemetry.NetworkBucket, decision);
        if (decision.Outcome == SecurityLimiterOutcome.Allowed)
        {
            await next(context);
            return;
        }

        if (decision.Outcome == SecurityLimiterOutcome.Limited)
        {
            logger.LogWarning("Security admission limited: {Code}",
                "security_rate_limit_exceeded");
            await SecurityAdmissionProblem.WriteAsync(context, localizer, decision);
            return;
        }

        logger.LogWarning("Security admission unavailable: {Code} {Reason}",
            "security_limiter_unavailable", StableReason(decision.Reason));
        await SecurityAdmissionProblem.WriteAsync(context, localizer, decision);
    }

    internal static bool TryGetTrustedClientAddress(
        HttpContext context,
        out IPAddress address)
    {
        address = Normalize(context.Connection.RemoteIpAddress) ?? IPAddress.None;
        return !string.IsNullOrWhiteSpace(address.ToString())
               && !IPAddress.Any.Equals(address)
               && !IPAddress.IPv6Any.Equals(address)
               && !IPAddress.None.Equals(address)
               && !IPAddress.IPv6None.Equals(address)
               && string.IsNullOrWhiteSpace(
                   context.Request.Headers["X-Forwarded-For"].ToString());
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;

    private static string StableReason(SecurityLimiterReason reason) => reason switch
    {
        SecurityLimiterReason.InvalidSubject => "invalid_subject",
        SecurityLimiterReason.RedisFailure => "redis_failure",
        SecurityLimiterReason.MalformedReply => "malformed_reply",
        _ => "unexpected",
    };
}
