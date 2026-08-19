namespace Saydin.Api.Security;

public enum SecurityLimiterOutcome
{
    Allowed,
    Limited,
    Unavailable,
}

public readonly record struct SecurityLimiterDecision(
    SecurityLimiterOutcome Outcome,
    TimeSpan RetryAfter)
{
    public static SecurityLimiterDecision Allowed { get; } =
        new(SecurityLimiterOutcome.Allowed, TimeSpan.Zero);

    public static SecurityLimiterDecision Unavailable { get; } =
        new(SecurityLimiterOutcome.Unavailable, TimeSpan.Zero);

    public static SecurityLimiterDecision Limited(TimeSpan retryAfter) =>
        new(SecurityLimiterOutcome.Limited, retryAfter);
}
