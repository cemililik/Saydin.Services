namespace Saydin.Shared.Exceptions;

/// <summary>
/// Senaryo bulunamadığında veya geçerli olmadığında fırlatılır.
/// `Message` teknik kullanım içindir (log/stack trace).
/// </summary>
public sealed class ScenarioNotFoundException(Guid scenarioId)
    : Exception($"Scenario '{scenarioId}' not found or does not belong to this user.")
{
    public Guid ScenarioId { get; } = scenarioId;
}
