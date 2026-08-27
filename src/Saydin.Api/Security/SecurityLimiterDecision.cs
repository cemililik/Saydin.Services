namespace Saydin.Api.Security;

public enum SecurityLimiterOutcome
{
    Allowed,
    Limited,
    Unavailable,
}

public enum SecurityLimiterReason
{
    Allowed,
    LimitExceeded,
    InvalidSubject,
    RedisFailure,
    MalformedReply,
}

public readonly record struct SecurityLimiterDecision(
    SecurityLimiterOutcome Outcome,
    TimeSpan RetryAfter,
    SecurityLimiterReason Reason)
{
    public static SecurityLimiterDecision Allowed { get; } =
        new(SecurityLimiterOutcome.Allowed, TimeSpan.Zero, SecurityLimiterReason.Allowed);

    public static SecurityLimiterDecision Unavailable { get; } =
        UnavailableFor(SecurityLimiterReason.RedisFailure);

    public static SecurityLimiterDecision Limited(TimeSpan retryAfter) =>
        new(SecurityLimiterOutcome.Limited, retryAfter, SecurityLimiterReason.LimitExceeded);

    public static SecurityLimiterDecision UnavailableFor(SecurityLimiterReason reason)
    {
        if (reason is not (SecurityLimiterReason.InvalidSubject
            or SecurityLimiterReason.RedisFailure
            or SecurityLimiterReason.MalformedReply))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new SecurityLimiterDecision(
            SecurityLimiterOutcome.Unavailable,
            reason == SecurityLimiterReason.InvalidSubject
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(5),
            reason);
    }
}
