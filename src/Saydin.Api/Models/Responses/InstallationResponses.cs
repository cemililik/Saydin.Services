namespace Saydin.Api.Models.Responses;

public sealed record InstallationRegistrationResponse(
    Guid PrincipalId,
    string Credential,
    string Scheme = "Installation")
{
    public override string ToString() =>
        $"InstallationRegistrationResponse {{ PrincipalId = {PrincipalId}, Credential = [REDACTED], Scheme = {Scheme} }}";
}

public sealed record InstallationRotationResponse(
    Guid RotationId,
    string Credential,
    string Scheme = "Installation")
{
    public override string ToString() =>
        $"InstallationRotationResponse {{ RotationId = {RotationId}, Credential = [REDACTED], Scheme = {Scheme} }}";
}
