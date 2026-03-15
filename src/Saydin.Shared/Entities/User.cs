namespace Saydin.Shared.Entities;

public sealed class User
{
    public Guid Id { get; init; }
    public string? DeviceId { get; init; }
    public string? Email { get; init; }
    public string Tier { get; init; } = "free";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; set; }

    // Navigation
    public ICollection<SavedScenario> SavedScenarios { get; init; } = [];
}
