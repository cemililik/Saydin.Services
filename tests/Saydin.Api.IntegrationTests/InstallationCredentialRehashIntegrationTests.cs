using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class InstallationCredentialRehashIntegrationTests(DatabaseFixture database)
{
    [SkippableFact]
    public async Task SuccessfulActiveResolve_AtomicallyRehashes_WhileEveryOtherLifecycleStateStaysImmutable()
    {
        Skip.IfNot(database.Available, database.SkipReason);
        Skip.IfNot(database.ApiTrust, "Migration 024 credential rehash contract is required.");
        var repository = database.CreateInstallationRepository();
        var principals = new List<Guid>();
        try
        {
            var live = await RegisterOldAsync(repository, principals, 0x11);
            var active = Candidate(2, 0x91);
            var candidates = new[] { active, live.Verifier };

            var concurrent = await Task.WhenAll(
                repository.ResolveAsync(candidates, active.KeyVersion, CancellationToken.None),
                repository.ResolveAsync(candidates, active.KeyVersion, CancellationToken.None));

            concurrent.Should().AllSatisfy(result =>
            {
                result.Should().NotBeNull();
                result!.PrincipalId.Should().Be(live.PrincipalId);
            });
            await AssertVerifierAsync(live.CredentialId, active);

            // The accepted-key set can now drop v1: the same bearer-derived active
            // verifier continues to authenticate after the on-use upgrade.
            (await repository.ResolveAsync([active], active.KeyVersion, CancellationToken.None))
                .Should().NotBeNull().And.Match<InstallationPrincipal>(result =>
                    result.PrincipalId == live.PrincipalId);

            var revoked = await RegisterOldAsync(repository, principals, 0x12);
            await repository.RevokeAsync(revoked.Verifier, CancellationToken.None);
            (await repository.ResolveAsync(
                    [Candidate(2, 0x92), revoked.Verifier], 2, CancellationToken.None))
                .Should().BeNull();
            await AssertVerifierAsync(revoked.CredentialId, revoked.Verifier);

            var expired = await RegisterOldAsync(repository, principals, 0x13);
            var issuedAt = DateTimeOffset.UtcNow.AddHours(-2);
            await using (var admin = database.CreateAdminContext())
                await admin.Set<InstallationCredential>()
                    .Where(item => item.Id == expired.CredentialId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.IssuedAt, issuedAt)
                        .SetProperty(item => item.ActivatedAt, issuedAt)
                        .SetProperty(item => item.ExpiresAt, issuedAt.AddHours(1)));
            (await repository.ResolveAsync(
                    [Candidate(2, 0x93), expired.Verifier], 2, CancellationToken.None))
                .Should().BeNull();
            await AssertVerifierAsync(expired.CredentialId, expired.Verifier);

            var rotating = await RegisterOldAsync(repository, principals, 0x14);
            var rotationId = Guid.CreateVersion7();
            var pendingId = Guid.CreateVersion7();
            var pending = Candidate(1, 0x24);
            await repository.BeginRotationAsync(
                rotating.Verifier, rotationId, pendingId, pending, CancellationToken.None);
            (await repository.ResolveAsync(
                    [Candidate(2, 0x94), pending], 2, CancellationToken.None))
                .Should().BeNull("pending credentials never enter normal active resolution");
            (await repository.ResolvePendingRotationAsync(
                    Guid.CreateVersion7(), [pending], CancellationToken.None))
                .Should().BeNull("a credential bound to another rotation id is rejected");
            await AssertVerifierAsync(pendingId, pending);
        }
        finally
        {
            await using var admin = database.CreateAdminContext();
            await admin.Users.Where(user => principals.Contains(user.Id)).ExecuteDeleteAsync();
        }
    }

    private static async Task<(Guid PrincipalId, Guid CredentialId, CredentialHashCandidate Verifier)>
        RegisterOldAsync(InstallationRepository repository, ICollection<Guid> cleanup, byte marker)
    {
        var principalId = Guid.CreateVersion7();
        var credentialId = Guid.CreateVersion7();
        var verifier = Candidate(1, marker);
        cleanup.Add(principalId);
        await repository.RegisterAsync(principalId, credentialId, verifier, CancellationToken.None);
        return (principalId, credentialId, verifier);
    }

    private async Task AssertVerifierAsync(Guid credentialId, CredentialHashCandidate expected)
    {
        await using var admin = database.CreateAdminContext();
        var stored = await admin.Set<InstallationCredential>().AsNoTracking()
            .SingleAsync(item => item.Id == credentialId);
        stored.HashKeyVersion.Should().Be(expected.KeyVersion);
        stored.SecretHash.Should().Equal(expected.SecretHash);
    }

    private static CredentialHashCandidate Candidate(short version, byte marker) =>
        new(version, Enumerable.Repeat(marker, 32).ToArray());
}
