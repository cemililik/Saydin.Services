namespace Saydin.PriceIngestion.Workers;

internal static class IngestionRetryBackoff
{
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(6);

    internal static TimeSpan Calculate(TimeSpan baseDelay, int attemptCount, Guid windowId)
    {
        if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;

        var exponent = Math.Clamp(attemptCount - 1, 0, 20);
        var exponentialTicks = baseDelay.Ticks > MaximumDelay.Ticks >> exponent
            ? MaximumDelay.Ticks
            : baseDelay.Ticks << exponent;

        Span<byte> identity = stackalloc byte[16];
        windowId.TryWriteBytes(identity);
        var jitterBasisPoints = 7_500 + ((identity[0] << 8 | identity[1]) % 5_001);
        var jitteredTicks = exponentialTicks > MaximumDelay.Ticks * 10_000L / jitterBasisPoints
            ? MaximumDelay.Ticks
            : exponentialTicks * jitterBasisPoints / 10_000L;
        return TimeSpan.FromTicks(Math.Min(jitteredTicks, MaximumDelay.Ticks));
    }
}
