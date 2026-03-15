namespace Saydin.Shared.Exceptions;

public sealed class ScenarioNotFoundException(Guid scenarioId)
    : Exception($"'{scenarioId}' id'li senaryo bulunamadı veya bu kullanıcıya ait değil.")
{
    public Guid ScenarioId { get; } = scenarioId;
}
