using System.Net;
using System.Text.Json;
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

    [Fact]
    public async Task PrincipalLimit_ReturnsProblem429WithoutInvokingEndpoint()
    {
        var principal = Principal();
        var endpointRan = false;
        var limiter = new ObservedLimiter(
            () => { }, SecurityLimiterDecision.Limited(TimeSpan.FromMilliseconds(1_001)));
        await using var app = BuildApp(Keyring(), new ResolvingRepository(principal), limiter);
        app.MapGet("/limited", () =>
            {
                endpointRan = true;
                return Results.NoContent();
            })
            .RequireInstallationCredential();

        var context = await InvokeAsync(app, "/limited");

        endpointRan.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.ContentType.Should().StartWith("application/problem+json");
        context.Response.Headers.RetryAfter.ToString().Should().Be("2");
        (await ReadCodeAsync(context)).Should().Be("security_rate_limited");
    }

    [Fact]
    public async Task PrincipalLimiterUnavailable_ReturnsProblem503WithoutInvokingEndpoint()
    {
        var principal = Principal();
        var endpointRan = false;
        var limiter = new ObservedLimiter(
            () => { }, SecurityLimiterDecision.UnavailableFor(
                SecurityLimiterReason.RedisFailure));
        await using var app = BuildApp(Keyring(), new ResolvingRepository(principal), limiter);
        app.MapGet("/unavailable", () =>
            {
                endpointRan = true;
                return Results.NoContent();
            })
            .RequireInstallationCredential();

        var context = await InvokeAsync(app, "/unavailable");

        endpointRan.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.ContentType.Should().StartWith("application/problem+json");
        context.Response.Headers.RetryAfter.ToString().Should().Be("5");
        (await ReadCodeAsync(context)).Should().Be("security_limiter_unavailable");
    }

    [Fact]
    public async Task RegistrationLimit_StopsHandlerBeforePrincipalRowsCanBeCreated()
    {
        var endpointRan = false;
        var limiter = new ObservedLimiter(
            () => { }, registrationDecision:
            SecurityLimiterDecision.Limited(TimeSpan.FromSeconds(17)));
        await using var app = BuildApp(Keyring(), new ResolvingRepository(Principal()), limiter);
        app.MapPost("/register", () =>
            {
                endpointRan = true;
                return Results.StatusCode(StatusCodes.Status201Created);
            })
            .RequireRegistrationAdmission();

        var context = await InvokeAsync(app, "/register");

        endpointRan.Should().BeFalse();
        limiter.RegistrationCalls.Should().Be(1);
        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().Should().Be("17");
        (await ReadCodeAsync(context)).Should().Be("security_rate_limited");
    }

    [Fact]
    public async Task RegistrationHandlerFailure_ReleasesReservedAdmissionBudget()
    {
        var limiter = new ObservedLimiter(() => { });
        await using var app = BuildApp(Keyring(), new ResolvingRepository(Principal()), limiter);
        app.MapPost(
                "/register-failure",
                (Func<IResult>)(() =>
                    throw new InvalidOperationException("registration-write-failed")))
            .RequireRegistrationAdmission();

        var action = () => InvokeAsync(app, "/register-failure");

        await action.Should().ThrowAsync<InvalidOperationException>();
        limiter.RegistrationCalls.Should().Be(1);
        limiter.RegistrationReleaseCalls.Should().Be(1);
    }

    [Fact]
    public async Task RegistrationPostCommitFailure_DoesNotReleaseConsumedAdmissionBudget()
    {
        var limiter = new ObservedLimiter(() => { });
        await using var app = BuildApp(Keyring(), new ResolvingRepository(Principal()), limiter);
        app.MapPost(
                "/register-post-commit-failure",
                (Func<HttpContext, IResult>)(http =>
                {
                    http.Items[EndpointExtensions.RegistrationCommittedItemKey] = true;
                    throw new InvalidOperationException("post-commit-decoration-failed");
                }))
            .RequireRegistrationAdmission();

        var action = () => InvokeAsync(app, "/register-post-commit-failure");

        await action.Should().ThrowAsync<InvalidOperationException>();
        limiter.RegistrationCalls.Should().Be(1);
        limiter.RegistrationReleaseCalls.Should().Be(0);
    }

    [Fact]
    public async Task CalculationNetworkLimit_IsCheckedAfterPrincipalAndDoesNotInvokeHandler()
    {
        var endpointRan = false;
        var limiter = new ObservedLimiter(
            () => { }, calculationDecision:
            SecurityLimiterDecision.Limited(TimeSpan.FromSeconds(9)));
        await using var app = BuildApp(Keyring(), new ResolvingRepository(Principal()), limiter);
        app.MapGet("/calculation", () =>
            {
                endpointRan = true;
                return Results.NoContent();
            })
            .RequireInstallationCredential(requireCalculationNetworkAdmission: true);

        var context = await InvokeAsync(app, "/calculation");

        endpointRan.Should().BeFalse();
        limiter.CalculationCalls.Should().Be(1);
        limiter.PrincipalCalls.Should().Be(1,
            "a principal-limited caller must never consume a shared network budget");
        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task PrincipalLimit_DoesNotConsumeCalculationNetworkBucket()
    {
        var limiter = new ObservedLimiter(
            () => { }, principalDecision:
            SecurityLimiterDecision.Limited(TimeSpan.FromSeconds(3)));
        await using var app = BuildApp(Keyring(), new ResolvingRepository(Principal()), limiter);
        app.MapGet("/principal-limited-calculation", () => Results.NoContent())
            .RequireInstallationCredential(requireCalculationNetworkAdmission: true);

        var context = await InvokeAsync(app, "/principal-limited-calculation");

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        limiter.PrincipalCalls.Should().Be(1);
        limiter.CalculationCalls.Should().Be(0);
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
        builder.Services.AddSingleton<IActivityPrincipalPseudonymizer>(
            new FixedPseudonymizer());
        builder.Services.AddLocalization();
        builder.Services.AddScoped<InstallationPrincipalContext>();
        builder.Services.AddScoped<IInstallationPrincipalContext>(services =>
            services.GetRequiredService<InstallationPrincipalContext>());
        return builder.Build();
    }

    private static InstallationPrincipal Principal() => new(
        Guid.NewGuid(), Guid.NewGuid(), 1, "free", "active", "active");

    private static ObservedKeyring Keyring() => new(
        Enumerable.Repeat((byte)0x41, 32).ToArray(),
        Enumerable.Repeat((byte)0x5a, 32).ToArray());

    private static async Task<string?> ReadCodeAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("code").GetString();
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

    }

    private sealed class FixedPseudonymizer : IActivityPrincipalPseudonymizer
    {
        public string Pseudonymize(Guid principalId) => "p1:test";
    }

    private sealed class ResolvingRepository(InstallationPrincipal principal)
        : IInstallationRepository
    {
        public Task<InstallationPrincipal?> ResolveAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            short activeKeyVersion,
            CancellationToken ct) => Task.FromResult<InstallationPrincipal?>(principal);
        public Task<InstallationPrincipal?> ResolvePendingRotationAsync(Guid rotationId,
            IReadOnlyList<CredentialHashCandidate> candidates, CancellationToken ct) =>
            Task.FromResult<InstallationPrincipal?>(principal);

        public Task<InstallationPrincipal> RegisterAsync(Guid principalId, Guid credentialId,
            CredentialHashCandidate credential, CancellationToken ct) => throw new NotSupportedException();
        public Task<InstallationPrincipal> BeginRotationAsync(CredentialHashCandidate currentCredential,
            Guid rotationId, Guid newCredentialId, CredentialHashCandidate newCredential,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<InstallationPrincipal> CommitRotationAsync(Guid rotationId,
            CredentialHashCandidate newCredential, CancellationToken ct) => throw new NotSupportedException();
        public Task<InstallationPrincipal> RevokeAsync(
            CredentialHashCandidate currentCredential, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ObservedLimiter(
        Action beforeReturn,
        SecurityLimiterDecision? principalDecision = null,
        SecurityLimiterDecision? registrationDecision = null,
        SecurityLimiterDecision? calculationDecision = null) : IDistributedSecurityLimiter
    {
        public int PrincipalCalls { get; private set; }
        public int RegistrationCalls { get; private set; }
        public int RegistrationReleaseCalls { get; private set; }
        public int CalculationCalls { get; private set; }

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
            return ValueTask.FromResult(
                principalDecision ?? SecurityLimiterDecision.Allowed);
        }

        public ValueTask<SecurityLimiterDecision> TryAcquireRegistrationAsync(
            IPAddress clientAddress,
            CancellationToken cancellationToken = default)
        {
            RegistrationCalls++;
            return ValueTask.FromResult(
                registrationDecision ?? SecurityLimiterDecision.Allowed);
        }

        public ValueTask ReleaseRegistrationAsync(IPAddress clientAddress)
        {
            RegistrationReleaseCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<SecurityLimiterDecision> TryAcquireCalculationNetworkAsync(
            IPAddress clientAddress,
            CancellationToken cancellationToken = default)
        {
            CalculationCalls++;
            return ValueTask.FromResult(
                calculationDecision ?? SecurityLimiterDecision.Allowed);
        }
    }
}
