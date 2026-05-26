namespace Saydin.Shared.Exceptions;

/// <summary>
/// Günlük hesaplama limitine ulaşıldığında fırlatılır.
/// `Message` teknik kullanım içindir (log/stack trace).
/// </summary>
public sealed class DailyLimitExceededException(int limit)
    : Exception($"Daily limit reached: {limit} calculations per day.")
{
    public int Limit { get; } = limit;
}
