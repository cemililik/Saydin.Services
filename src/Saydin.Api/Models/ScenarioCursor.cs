namespace Saydin.Api.Models;

/// <summary>
/// Stable saved-scenario keyset. PostgreSQL query ordering is
/// <c>(created_at, id) DESC</c> and therefore the cursor must carry both values.
/// </summary>
public readonly record struct ScenarioCursor(DateTimeOffset CreatedAt, Guid Id);
