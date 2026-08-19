namespace Saydin.Shared.Entities;

/// <summary>
/// A server-side verifier for an opaque installation credential. The raw
/// credential is never persisted; <see cref="SecretHash"/> contains only the
/// keyed digest produced by the API credential keyring.
/// </summary>
public sealed class InstallationCredential
{
    public Guid Id { get; init; }
    public Guid PrincipalId { get; init; }
    public int Generation { get; init; }
    public byte[] SecretHash { get; init; } = [];
    public short HashKeyVersion { get; init; }
    public string State { get; init; } = "pending";
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset? PendingExpiresAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? ActivatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public Guid? RotationParentId { get; init; }
    public Guid? RotationId { get; init; }

    public User Principal { get; init; } = default!;
    public InstallationCredential? RotationParent { get; init; }
}
