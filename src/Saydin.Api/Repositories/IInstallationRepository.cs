using Saydin.Api.Services;

namespace Saydin.Api.Repositories;

public interface IInstallationRepository
{
    Task<InstallationPrincipal> RegisterAsync(
        Guid principalId,
        Guid credentialId,
        CredentialHashCandidate credential,
        CancellationToken ct);

    Task<InstallationPrincipal?> ResolveAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        short activeKeyVersion,
        CancellationToken ct);

    Task<InstallationPrincipal?> ResolvePendingRotationAsync(
        Guid rotationId,
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken ct);

    Task<InstallationPrincipal> BeginRotationAsync(
        CredentialHashCandidate currentCredential,
        Guid rotationId,
        Guid newCredentialId,
        CredentialHashCandidate newCredential,
        CancellationToken ct);

    Task<InstallationPrincipal> CommitRotationAsync(
        Guid rotationId,
        CredentialHashCandidate newCredential,
        CancellationToken ct);

    Task<InstallationPrincipal> RevokeAsync(
        CredentialHashCandidate currentCredential,
        CancellationToken ct);
}
