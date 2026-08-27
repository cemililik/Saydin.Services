using System.Diagnostics;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Exceptions;
using Saydin.Api.Middleware;
using Saydin.Api.Services;
using Saydin.Api.Repositories;
using Saydin.Api.Security;
using System.Security.Cryptography;
using System.Net;

namespace Saydin.Api.Endpoints;

internal static class EndpointExtensions
{
    internal const string PrincipalActivityIdItemKey = "InstallationPrincipalActivityId";
    internal const string RegistrationCommittedItemKey = "InstallationRegistrationCommitted";

    internal static RouteHandlerBuilder RequireInstallationCredential(
        this RouteHandlerBuilder builder,
        bool requireCalculationNetworkAdmission = false)
    {
        // Every secured route can terminate in these three filter-level outcomes;
        // keep generated clients aligned with the runtime contract at the filter boundary.
        builder.ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var keyring = http.RequestServices.GetRequiredService<IInstallationCredentialKeyring>();
            var repository = http.RequestServices.GetRequiredService<IInstallationRepository>();

            if (!TryReadInstallationToken(http.Request, keyring, out var secret))
                return InvalidInstallationCredential(http);

            IReadOnlyList<CredentialHashCandidate>? candidates = null;
            InstallationPrincipal? principal = null;
            try
            {
                candidates = keyring.HashAccepted(secret);
                principal = await repository.ResolveAsync(
                    candidates, keyring.ActiveKeyVersion, http.RequestAborted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
                ZeroCandidateHashes(candidates);
            }

            // No decoded credential or verifier remains live while application code runs.
            if (principal is null)
                return InvalidInstallationCredential(http);

            http.RequestServices.GetRequiredService<InstallationPrincipalContext>()
                .Set(principal);
            http.Items[PrincipalActivityIdItemKey] = http.RequestServices
                .GetRequiredService<IActivityPrincipalPseudonymizer>()
                .Pseudonymize(principal.PrincipalId);

            if (!LimiterEnabled(http))
                return await next(ctx);

            var limiter = http.RequestServices.GetRequiredService<IDistributedSecurityLimiter>();
            IPAddress? calculationAddress = null;
            if (requireCalculationNetworkAdmission)
            {
                if (!DistributedSecurityLimiterMiddleware.TryGetTrustedClientAddress(
                        http, out calculationAddress))
                    return UntrustedAddressProblem(http, SecurityAdmissionTelemetry.CalculationNetworkBucket,
                        "CalculationNetworkAdmission");
            }

            var principalAdmission = await limiter
                .TryAcquirePrincipalAsync(principal.PrincipalId, http.RequestAborted);
            var principalProblem = AdmissionProblem(
                http, principalAdmission, SecurityAdmissionTelemetry.PrincipalBucket,
                "InstallationPrincipalAdmission");
            if (principalProblem is not null)
                return principalProblem;

            if (calculationAddress is not null)
            {
                var calculationAdmission = await limiter.TryAcquireCalculationNetworkAsync(
                    calculationAddress, http.RequestAborted);
                var calculationProblem = AdmissionProblem(
                    http, calculationAdmission, SecurityAdmissionTelemetry.CalculationNetworkBucket,
                    "CalculationNetworkAdmission");
                if (calculationProblem is not null)
                    return calculationProblem;
            }

            return await next(ctx);
        });
    }

    internal static RouteHandlerBuilder RequireRegistrationAdmission(
        this RouteHandlerBuilder builder)
    {
        builder.ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!LimiterEnabled(http))
                return await next(ctx);
            if (!DistributedSecurityLimiterMiddleware.TryGetTrustedClientAddress(
                    http, out var clientAddress))
                return UntrustedAddressProblem(http, SecurityAdmissionTelemetry.RegistrationBucket,
                    "InstallationRegistrationAdmission");

            var limiter = http.RequestServices.GetRequiredService<IDistributedSecurityLimiter>();
            var decision = await limiter.TryAcquireRegistrationAsync(
                clientAddress, http.RequestAborted);
            var problem = AdmissionProblem(
                http, decision, SecurityAdmissionTelemetry.RegistrationBucket,
                "InstallationRegistrationAdmission");
            if (problem is not null) return problem;
            try
            {
                return await next(ctx);
            }
            catch
            {
                if (!http.Items.ContainsKey(RegistrationCommittedItemKey))
                    await limiter.ReleaseRegistrationAsync(clientAddress);
                throw;
            }
        });
    }

    internal static RouteHandlerBuilder RequirePendingInstallationCredential(
        this RouteHandlerBuilder builder)
    {
        builder.ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var request = ctx.Arguments.OfType<Models.Requests.InstallationRotationCommitRequest>()
                .SingleOrDefault();
            var keyring = http.RequestServices.GetRequiredService<IInstallationCredentialKeyring>();
            if (request is null || request.RotationId == Guid.Empty
                || !TryReadInstallationToken(http.Request, keyring, out var secret))
                return InvalidInstallationCredential(http);

            IReadOnlyList<CredentialHashCandidate>? candidates = null;
            InstallationPrincipal? principal = null;
            try
            {
                candidates = keyring.HashAccepted(secret);
                principal = await http.RequestServices.GetRequiredService<IInstallationRepository>()
                    .ResolvePendingRotationAsync(request.RotationId, candidates, http.RequestAborted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
                ZeroCandidateHashes(candidates);
            }

            if (principal is null)
                return InvalidInstallationCredential(http);

            http.RequestServices.GetRequiredService<InstallationPrincipalContext>().Set(principal);
            http.Items[PrincipalActivityIdItemKey] = http.RequestServices
                .GetRequiredService<IActivityPrincipalPseudonymizer>()
                .Pseudonymize(principal.PrincipalId);

            if (!LimiterEnabled(http))
                return await next(ctx);
            var decision = await http.RequestServices
                .GetRequiredService<IDistributedSecurityLimiter>()
                .TryAcquirePrincipalAsync(principal.PrincipalId, http.RequestAborted);
            var problem = AdmissionProblem(
                http, decision, SecurityAdmissionTelemetry.PrincipalBucket,
                "InstallationPendingCommitAdmission");
            return problem ?? await next(ctx);
        });
    }

    internal static bool TryReadInstallationToken(
        HttpRequest request,
        IInstallationCredentialKeyring keyring,
        out byte[] secret)
    {
        secret = [];
        var values = request.Headers.Authorization;
        if (values.Count != 1)
            return false;

        var value = values[0];
        const string prefix = "Installation ";
        if (value is null
            || value.Length != prefix.Length + InstallationCredentialKeyring.CredentialTextLength
            || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return keyring.TryDecode(value[prefix.Length..], out secret);
    }

    internal static IResult InvalidInstallationCredential(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Installation";
        var localizer = http.RequestServices.GetRequiredService<IStringLocalizer<ErrorMessages>>();
        return Results.Problem(
            title: localizer["InstallationCredentialInvalid"],
            detail: localizer["InstallationCredentialInvalidDetail"],
            statusCode: StatusCodes.Status401Unauthorized,
            type: "https://saydin.app/errors/invalid-installation-credential",
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
                ["code"] = ApiErrorCodes.InvalidInstallationCredential,
            });
    }

    internal static void ZeroCandidateHashes(IReadOnlyList<CredentialHashCandidate>? candidates)
    {
        if (candidates is null)
            return;

        foreach (var candidate in candidates)
            CryptographicOperations.ZeroMemory(candidate.SecretHash);
    }

    private static bool LimiterEnabled(HttpContext http) =>
        http.RequestServices.GetService<IOptions<DistributedSecurityLimiterOptions>>()
            ?.Value.Enabled ?? true;

    private static IResult? AdmissionProblem(
        HttpContext http,
        SecurityLimiterDecision decision,
        string bucket,
        string loggerName)
    {
        SecurityAdmissionTelemetry.Record(bucket, decision);
        if (decision.Outcome == SecurityLimiterOutcome.Allowed)
            return null;

        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(loggerName)
            .LogWarning("Security admission rejected: {Code} {Reason}",
                decision.Outcome == SecurityLimiterOutcome.Limited
                    ? "security_rate_limit_exceeded"
                    : "security_limiter_unavailable",
                StableReason(decision.Reason));
        return SecurityAdmissionProblem.Result(
            http,
            http.RequestServices.GetRequiredService<IStringLocalizer<ErrorMessages>>(),
            decision);
    }

    private static IResult UntrustedAddressProblem(
        HttpContext http,
        string bucket,
        string loggerName)
    {
        SecurityAdmissionTelemetry.Record(
            bucket, "unavailable", SecurityAdmissionTelemetry.ClientAddressUntrustedReason);
        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(loggerName)
            .LogWarning("Security admission rejected: {Code}",
                "security_client_address_untrusted");
        return SecurityAdmissionProblem.Result(
            http,
            http.RequestServices.GetRequiredService<IStringLocalizer<ErrorMessages>>(),
            SecurityLimiterDecision.UnavailableFor(SecurityLimiterReason.InvalidSubject));
    }

    private static string StableReason(SecurityLimiterReason reason) => reason switch
    {
        SecurityLimiterReason.Allowed => "allowed",
        SecurityLimiterReason.LimitExceeded => "limit_exceeded",
        SecurityLimiterReason.InvalidSubject => "invalid_subject",
        SecurityLimiterReason.RedisFailure => "redis_failure",
        SecurityLimiterReason.MalformedReply => "malformed_reply",
        _ => "unexpected",
    };
}
