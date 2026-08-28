using System.Security.Cryptography;
using Npgsql;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Middleware;
using Saydin.Api.Repositories;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class InstallationEndpoints
{
    public static IEndpointRouteBuilder MapInstallationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/installations").WithTags("Installations");

        group.MapPost("", RegisterAsync)
            .WithName("RegisterInstallation")
            .Produces<InstallationRegistrationResponse>(StatusCodes.Status201Created)
            .RequireRegistrationAdmission();

        group.MapPost("/rotation", BeginRotationAsync)
            .WithName("BeginInstallationRotation")
            .Produces<InstallationRotationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential();

        group.MapPost("/rotation/commit", CommitRotationAsync)
            .WithName("CommitInstallationRotation")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequirePendingInstallationCredential();

        group.MapDelete("/current", RevokeCurrentAsync)
            .WithName("RevokeInstallation")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential();

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext http,
        IInstallationCredentialKeyring keyring,
        IInstallationRepository repository,
        CancellationToken ct)
    {
        using var generated = keyring.Generate();
        var hash = keyring.HashActive(generated.Secret);
        try
        {
            var registered = await repository.RegisterAsync(Guid.NewGuid(), Guid.NewGuid(), hash, ct);
            // The admission filter may compensate a failed pre-commit request. Mark the
            // durable boundary before any post-commit decoration can throw so a created
            // principal never receives an unearned quota refund.
            http.Items[EndpointExtensions.RegistrationCommittedItemKey] = true;
            http.RequestServices.GetRequiredService<InstallationPrincipalContext>().Set(registered);
            http.GetOrCreateActivityLog(Saydin.Shared.Constants.ActivityActions.InstallationRegister)
                .WithUserId(registered.PrincipalId)
                .WithData(new { registered.Generation, registered.CredentialState });
            SetNoStore(http.Response);
            return Results.Created(
                "/v1/installations/current",
                new InstallationRegistrationResponse(registered.PrincipalId, generated.Token));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash.SecretHash);
        }
    }

    private static async Task<IResult> BeginRotationAsync(
        HttpContext http,
        IInstallationCredentialKeyring keyring,
        IInstallationRepository repository,
        CancellationToken ct)
    {
        if (!EndpointExtensions.TryReadInstallationToken(http.Request, keyring, out var currentSecret))
            return EndpointExtensions.InvalidInstallationCredential(http);

        using var generated = keyring.Generate();
        var pendingHash = keyring.HashActive(generated.Secret);
        var rotationId = Guid.NewGuid();
        IReadOnlyList<CredentialHashCandidate>? candidates = null;
        try
        {
            candidates = keyring.HashAccepted(currentSecret);
            foreach (var candidate in candidates)
            {
                try
                {
                    var rotated = await repository.BeginRotationAsync(
                        candidate,
                        rotationId,
                        Guid.NewGuid(),
                        pendingHash,
                        ct);
                    http.GetOrCreateActivityLog(
                            Saydin.Shared.Constants.ActivityActions.InstallationRotationBegin)
                        .WithData(new { rotated.Generation, rotated.CredentialState });
                    SetNoStore(http.Response);
                    return Results.Ok(new InstallationRotationResponse(rotationId, generated.Token));
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidAuthorizationSpecification)
                {
                    // Try the next accepted verifier key version without revealing which
                    // version (if any) matched the bearer credential.
                }
            }

            return EndpointExtensions.InvalidInstallationCredential(http);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentSecret);
            EndpointExtensions.ZeroCandidateHashes(candidates);
            CryptographicOperations.ZeroMemory(pendingHash.SecretHash);
        }
    }

    private static async Task<IResult> CommitRotationAsync(
        InstallationRotationCommitRequest request,
        HttpContext http,
        IInstallationCredentialKeyring keyring,
        IInstallationPrincipalContext principalContext,
        IInstallationRepository repository,
        CancellationToken ct)
    {
        if (request.RotationId == Guid.Empty
            || !EndpointExtensions.TryReadInstallationToken(http.Request, keyring, out var secret))
        {
            return EndpointExtensions.InvalidInstallationCredential(http);
        }

        IReadOnlyList<CredentialHashCandidate>? candidates = null;
        try
        {
            candidates = keyring.HashAccepted(secret);
            foreach (var candidate in candidates)
            {
                try
                {
                    var committed = await repository.CommitRotationAsync(
                        request.RotationId, candidate, ct);
                    if (!principalContext.IsResolved
                        || committed.PrincipalId != principalContext.PrincipalId)
                        throw new InvalidOperationException(
                            "installation_rotation_commit_principal_mismatch");
                    http.GetOrCreateActivityLog(
                            Saydin.Shared.Constants.ActivityActions.InstallationRotationCommit)
                        .WithData(new { committed.Generation, committed.CredentialState });
                    SetNoStore(http.Response);
                    return Results.NoContent();
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidAuthorizationSpecification)
                {
                    // The credential may belong to another accepted key version. The
                    // externally visible result stays generic after all candidates fail.
                }
            }

            return EndpointExtensions.InvalidInstallationCredential(http);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (candidates is not null)
            {
                foreach (var candidate in candidates)
                    CryptographicOperations.ZeroMemory(candidate.SecretHash);
            }
        }
    }

    private static async Task<IResult> RevokeCurrentAsync(
        HttpContext http,
        IInstallationCredentialKeyring keyring,
        IInstallationRepository repository,
        CancellationToken ct)
    {
        if (!EndpointExtensions.TryReadInstallationToken(http.Request, keyring, out var secret))
            return EndpointExtensions.InvalidInstallationCredential(http);

        IReadOnlyList<CredentialHashCandidate>? candidates = null;
        try
        {
            candidates = keyring.HashAccepted(secret);
            foreach (var candidate in candidates)
            {
                try
                {
                    var revoked = await repository.RevokeAsync(candidate, ct);
                    http.GetOrCreateActivityLog(
                            Saydin.Shared.Constants.ActivityActions.InstallationRevoke)
                        .WithData(new { revoked.Generation, revoked.CredentialState });
                    SetNoStore(http.Response);
                    return Results.NoContent();
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidAuthorizationSpecification)
                {
                    // Preserve the single generic authorization failure contract.
                }
            }

            return EndpointExtensions.InvalidInstallationCredential(http);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            EndpointExtensions.ZeroCandidateHashes(candidates);
        }
    }

    private static void SetNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
