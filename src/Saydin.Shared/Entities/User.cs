namespace Saydin.Shared.Entities;

public sealed class User
{
    public Guid Id { get; init; }
    public string? DeviceId { get; init; }
    public string? Email { get; init; }
    public string Tier { get; init; } = "free";
    public string PrincipalStatus { get; init; } = "legacy_quarantined";
    public int PrincipalContractVersion { get; init; } = 1;
    public DateTimeOffset? PrincipalQuarantinedAt { get; init; }
    public DateTimeOffset? PrincipalRevokedAt { get; init; }
    public DateTimeOffset? PrincipalExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; set; }

    // Navigation
    public ICollection<SavedScenario> SavedScenarios { get; init; } = [];
    public ICollection<InstallationCredential> InstallationCredentials { get; init; } = [];
}
