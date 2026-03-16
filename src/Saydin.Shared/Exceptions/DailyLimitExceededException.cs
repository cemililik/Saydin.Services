namespace Saydin.Shared.Exceptions;

public sealed class DailyLimitExceededException(int limit)
    : Exception($"Günlük {limit} hesaplama limitine ulaştınız. Yarın tekrar deneyin.")
{
    public int Limit { get; } = limit;
}
