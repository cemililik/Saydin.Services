using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Saydin.Api.Endpoints;
using Saydin.Api.Repositories;
using Saydin.Api.Security;
using Saydin.Api.Services;

namespace Saydin.Api.Tests.Endpoints;

public sealed class InstallationAuthenticationFilterTests
{
    [Fact]
    public async Task ResolvedCredentialBuffers_AreZeroedBeforePrincipalAdmissionAndEndpointCode()
    {
        var decoded = Enumerable.Repeat((byte)0x41, 32).ToArray();
        var verifier = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        var principal = new InstallationPrincipal(
            Guid.NewGuid(), Guid.NewGuid(), 1, "free", "active", "active");
        var keyring = new ObservedKeyring(decoded, verifier);
        var endpointRan = false;
        var limiter = new ObservedLimiter(() =>
        {
            decoded.Should().OnlyContain(value => value == 0);
            verifier.Should().OnlyContain(value => value == 0);
        });

        await using var app = BuildApp(keyring, new ResolvingRepository(principal), limiter);
        app.MapGet("/private", () =>
            {
                endpointRan = true;
                decoded.Should().OnlyContain(value => value == 0);
                verifier.Should().OnlyContain(value => value == 0);
                return Results.NoContent();
            })
            .RequireInstallationCredential();

        var context = await InvokeAsync(app, "/private");

        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        endpointRan.Should().BeTrue();
        limiter.PrincipalCalls.Should().Be(1);
    }

    private static WebApplication BuildApp(
        IInstallationCredentialKeyring keyring,
        IInstallationRepository repository,
        IDistributedSecurityLimiter limiter)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(keyring);
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton(limiter);
        builder.Services.AddScoped<InstallationPrincipalContext>();
        builder.Services.AddScoped<IInstallationPrincipalContext>(services =>
            services.GetRequiredService<InstallationPrincipalContext>());
        return builder.Build();
    }

    private static async Task<DefaultHttpContext> InvokeAsync(WebApplication app, string path)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .Single(item => item.DisplayName?.Contains(path, StringComparison.Ordinal) == true);
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Path = path;
        context.Request.Headers.Authorization = $"Installation {new string('A', 43)}";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        return context;
    }

    private sealed class ObservedKeyring(byte[] decoded, byte[] verifier)
        : IInstallationCredentialKeyring
    {
        public short ActiveKeyVersion => 1;
        public GeneratedInstallationCredential Generate() => throw new NotSupportedException();

        public bool TryDecode(string token, out byte[] secret)
        {
            secret = decoded;
            return true;
        }

        public CredentialHashCandidate HashActive(ReadOnlySpan<byte> secret) =>
            throw new NotSupportedException();

        public IReadOnlyList<CredentialHashCandidate> HashAccepted(ReadOnlySpan<byte> secret) =>
            [new CredentialHashCandidate(1, verifier)];

        public string PseudonymizePrincipal(Guid principalId) => "p1:test";
    }

    private sealed class ResolvingRepository(InstallationPrincipal principal)
        : IInstallationRepository
    {
        public Task<InstallationPrincipal?> ResolveAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            CancellationToken ct) => Task.FromResult<InstallationPrincipal?>(principal);

        public Task<InstallationPrincipal> RegisterAsync(Guid principalId, Guid credentialId,
            CredentialHashCandidate credential, CancellationToken ct) => throw new NotSupportedException();
        public Task<InstallationPrincipal> BeginRotationAsync(CredentialHashCandidate currentCredential,
            Guid rotationId, Guid newCredentialId, CredentialHashCandidate newCredential,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<InstallationPrincipal> CommitRotationAsync(Guid rotationId,
            CredentialHashCandidate newCredential, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAsync(CredentialHashCandidate currentCredential, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ObservedLimiter(Action beforeReturn) : IDistributedSecurityLimiter
    {
        public int PrincipalCalls { get; private set; }

        public ValueTask<SecurityLimiterDecision> TryAcquireNetworkAsync(
            IPAddress clientAddress,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SecurityLimiterDecision> TryAcquirePrincipalAsync(
            Guid principalId,
            CancellationToken cancellationToken = default)
        {
            PrincipalCalls++;
            beforeReturn();
            return ValueTask.FromResult(SecurityLimiterDecision.Allowed);
        }
    }
}
