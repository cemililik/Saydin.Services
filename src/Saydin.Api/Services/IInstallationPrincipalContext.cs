namespace Saydin.Api.Services;

public sealed record InstallationPrincipal(
    Guid PrincipalId,
    Guid CredentialId,
    int Generation,
    string Tier,
    string PrincipalStatus,
    string CredentialState);

public interface IInstallationPrincipalContext
{
    bool IsResolved { get; }
    InstallationPrincipal Principal { get; }
    Guid PrincipalId { get; }
    string Tier { get; }
}

public sealed class InstallationPrincipalContext : IInstallationPrincipalContext
{
    private InstallationPrincipal? _principal;

    public bool IsResolved => _principal is not null;
    public InstallationPrincipal Principal => _principal
        ?? throw new InvalidOperationException("Installation principal was not resolved for this request.");
    public Guid PrincipalId => Principal.PrincipalId;
    public string Tier => Principal.Tier;

    internal void Set(InstallationPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (_principal is not null)
            throw new InvalidOperationException("Installation principal cannot be replaced within one request.");
        if (principal.PrincipalId == Guid.Empty || principal.CredentialId == Guid.Empty)
            throw new ArgumentException("Installation principal identity is invalid.", nameof(principal));
        _principal = principal;
    }
}
