using Saydin.Shared.Diagnostics;

namespace Saydin.Api.Security;

internal static class SecurityAdmissionTelemetry
{
    internal const string NetworkBucket = "network";
    internal const string PrincipalBucket = "principal";
    internal const string RegistrationBucket = "registration";
    internal const string CalculationNetworkBucket = "calculation_network";
    internal const string ClientAddressUntrustedReason = "client_address_untrusted";

    internal static void Record(string bucket, SecurityLimiterDecision decision) =>
        Record(bucket, Outcome(decision.Outcome), Reason(decision.Reason));

    internal static void Record(string bucket, string outcome, string reason)
    {
        if (bucket is not (NetworkBucket or PrincipalBucket or RegistrationBucket
            or CalculationNetworkBucket))
            throw new ArgumentOutOfRangeException(nameof(bucket));
        if (outcome is not ("allowed" or "limited" or "unavailable"))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (reason is not ("allowed" or "limit_exceeded" or "invalid_subject"
            or "redis_failure" or "malformed_reply" or ClientAddressUntrustedReason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        SaydinMetrics.SecurityAdmissionDecisions.Add(1,
            new KeyValuePair<string, object?>("bucket", bucket),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("reason", reason));
    }

    private static string Outcome(SecurityLimiterOutcome outcome) => outcome switch
    {
        SecurityLimiterOutcome.Allowed => "allowed",
        SecurityLimiterOutcome.Limited => "limited",
        SecurityLimiterOutcome.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string Reason(SecurityLimiterReason reason) => reason switch
    {
        SecurityLimiterReason.Allowed => "allowed",
        SecurityLimiterReason.LimitExceeded => "limit_exceeded",
        SecurityLimiterReason.InvalidSubject => "invalid_subject",
        SecurityLimiterReason.RedisFailure => "redis_failure",
        SecurityLimiterReason.MalformedReply => "malformed_reply",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
