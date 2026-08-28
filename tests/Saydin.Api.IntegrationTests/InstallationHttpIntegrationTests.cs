using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class InstallationHttpIntegrationTests(
    DatabaseFixture database,
    ErrorContractWebAppFactory factory) : IClassFixture<ErrorContractWebAppFactory>
{
    [SkippableFact]
    public async Task RegistrationRotationCommitAndRevoke_EnforceOpaqueLifecycleAndGenericFailures()
    {
        Skip.IfNot(database.Available, database.SkipReason);
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        Skip.IfNot(await ErrorContractHttpTests.ApiTrustReadyAsync(database),
            "Migration 021 API trust contract is required.");

        var client = factory.CreateClient();
        var registration = await ErrorContractHttpTests.RegisterAsync(client);
        registration.PrincipalId.Should().NotBeEmpty();
        registration.Scheme.Should().Be("Installation");
        registration.Credential.Should().HaveLength(43)
            .And.MatchRegex("^[A-Za-z0-9_-]{43}$");

        var rawCredential = Decode(registration.Credential);
        try
        {
            await using var db = database.CreateAdminContext();
            var principal = await db.Users.AsNoTracking()
                .SingleAsync(user => user.Id == registration.PrincipalId);
            principal.DeviceId.Should().BeNull();
            principal.PrincipalStatus.Should().Be("active");
            var active = await db.Set<InstallationCredential>().AsNoTracking()
                .SingleAsync(credential => credential.PrincipalId == registration.PrincipalId);
            active.State.Should().Be("active");
            active.Generation.Should().Be(1);
            active.SecretHash.Should().HaveCount(32).And.NotEqual(rawCredential);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawCredential);
        }

        (await SendAuthorizedAsync(client, HttpMethod.Get, "/v1/config", registration.Credential))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var firstBegin = await SendAuthorizedAsync(
            client, HttpMethod.Post, "/v1/installations/rotation", registration.Credential);
        firstBegin.StatusCode.Should().Be(HttpStatusCode.OK);
        firstBegin.Headers.CacheControl!.NoStore.Should().BeTrue();
        var firstPending = (await firstBegin.Content
            .ReadFromJsonAsync<InstallationRotationResponse>())!;

        // A lost begin response never invalidates the old active credential. A new
        // begin supersedes only the previous pending verifier.
        (await SendAuthorizedAsync(client, HttpMethod.Get, "/v1/config", registration.Credential))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBegin = await SendAuthorizedAsync(
            client, HttpMethod.Post, "/v1/installations/rotation", registration.Credential);
        secondBegin.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondPending = (await secondBegin.Content
            .ReadFromJsonAsync<InstallationRotationResponse>())!;
        secondPending.RotationId.Should().NotBe(firstPending.RotationId);
        secondPending.Credential.Should().NotBe(firstPending.Credential);

        var supersededCommit = await CommitAsync(client, firstPending);
        await AssertGenericUnauthorizedAsync(supersededCommit);

        var commit = await CommitAsync(client, secondPending);
        commit.StatusCode.Should().Be(HttpStatusCode.NoContent);
        commit.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await CommitAsync(client, secondPending)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertGenericUnauthorizedAsync(await SendAuthorizedAsync(
            client, HttpMethod.Get, "/v1/config", registration.Credential));
        (await SendAuthorizedAsync(client, HttpMethod.Get, "/v1/config", secondPending.Credential))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var revoke = await SendAuthorizedAsync(
            client, HttpMethod.Delete, "/v1/installations/current", secondPending.Credential);
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        revoke.Headers.CacheControl!.NoStore.Should().BeTrue();
        await AssertGenericUnauthorizedAsync(await SendAuthorizedAsync(
            client, HttpMethod.Get, "/v1/config", secondPending.Credential));
    }

    [SkippableFact]
    public async Task LegacyDeviceHeaderAndMalformedOrUnknownCredentials_ShareOne401Contract()
    {
        Skip.IfNot(database.Available, database.SkipReason);
        Skip.IfNot(factory.InfraAvailable, factory.SkipReason);
        Skip.IfNot(await ErrorContractHttpTests.ApiTrustReadyAsync(database),
            "Migration 021 API trust contract is required.");

        var client = factory.CreateClient();
        using var legacyOnly = new HttpRequestMessage(HttpMethod.Get, "/v1/scenarios");
        legacyOnly.Headers.TryAddWithoutValidation("X-Device-ID", $"legacy-{Guid.NewGuid():N}");
        await AssertGenericUnauthorizedAsync(await client.SendAsync(legacyOnly));

        await AssertGenericUnauthorizedAsync(await SendAuthorizedAsync(
            client, HttpMethod.Get, "/v1/scenarios", "not-a-canonical-credential"));
        await AssertGenericUnauthorizedAsync(await SendAuthorizedAsync(
            client, HttpMethod.Get, "/v1/scenarios",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_')));
    }

    private static async Task<HttpResponseMessage> CommitAsync(
        HttpClient client,
        InstallationRotationResponse pending)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/installations/rotation/commit")
        {
            Content = JsonContent.Create(new InstallationRotationCommitRequest(pending.RotationId)),
        };
        ErrorContractHttpTests.Authorize(request, pending.Credential);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string credential)
    {
        using var request = new HttpRequestMessage(method, path);
        ErrorContractHttpTests.Authorize(request, credential);
        return await client.SendAsync(request);
    }

    private static async Task AssertGenericUnauthorizedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle(value => value.Scheme == "Installation");
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_installation_credential");
        body.GetProperty("type").GetString().Should()
            .Be("https://saydin.app/errors/invalid-installation-credential");
        body.GetRawText().ToLowerInvariant().Should().NotContain("hash")
            .And.NotContain("version")
            .And.NotContain("revoked");
    }

    private static byte[] Decode(string credential) => Convert.FromBase64String(
        string.Concat(credential.Replace('-', '+').Replace('_', '/'), "="));
}
