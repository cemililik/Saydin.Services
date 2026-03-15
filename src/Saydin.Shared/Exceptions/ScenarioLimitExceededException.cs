namespace Saydin.Shared.Exceptions;

public sealed class ScenarioLimitExceededException(int limit)
    : Exception($"Ücretsiz kullanıcılar en fazla {limit} senaryo kaydedebilir.")
{
    public int Limit { get; } = limit;
}
