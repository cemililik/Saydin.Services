namespace Saydin.Shared.Exceptions;

/// <summary>
/// Kayıtlı senaryo sayısı tier limitini aştığında fırlatılır.
/// `Message` teknik kullanım içindir (log/stack trace).
/// </summary>
public sealed class ScenarioLimitExceededException(int limit)
    : Exception($"Scenario limit exceeded: free tier allows at most {limit} saved scenarios.")
{
    public int Limit { get; } = limit;
}
