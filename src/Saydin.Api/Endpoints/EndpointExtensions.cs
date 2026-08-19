using System.Diagnostics;
using Microsoft.Extensions.Localization;
using Saydin.Api.Exceptions;
using Saydin.Api.Services;
using Saydin.Api.Repositories;
using Saydin.Api.Security;
using System.Security.Cryptography;

namespace Saydin.Api.Endpoints;

internal static class EndpointExtensions
{
    internal const string PrincipalActivityIdItemKey = "InstallationPrincipalActivityId";

    internal static RouteHandlerBuilder RequireInstallationCredential(this RouteHandlerBuilder builder)
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
                principal = await repository.ResolveAsync(candidates, http.RequestAborted);
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
            http.Items[PrincipalActivityIdItemKey] =
                keyring.PseudonymizePrincipal(principal.PrincipalId);

            var principalAdmission = await http.RequestServices
                .GetRequiredService<IDistributedSecurityLimiter>()
                .TryAcquirePrincipalAsync(principal.PrincipalId, http.RequestAborted);
            if (principalAdmission.Outcome == SecurityLimiterOutcome.Limited)
            {
                var retrySeconds = Math.Max(
                    1,
                    (int)Math.Ceiling(principalAdmission.RetryAfter.TotalSeconds));
                http.Response.Headers.RetryAfter = retrySeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return SecurityLimiterProblem(
                    http,
                    StatusCodes.Status429TooManyRequests,
                    "security_rate_limited",
                    "https://saydin.app/errors/security-rate-limited",
                    "Too many requests.");
            }

            if (principalAdmission.Outcome != SecurityLimiterOutcome.Allowed)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("InstallationPrincipalAdmission")
                    .LogWarning(
                        "Distributed security limiter unavailable: {Code}",
                        "security_limiter_unavailable");
                return SecurityLimiterProblem(
                    http,
                    StatusCodes.Status503ServiceUnavailable,
                    "security_limiter_unavailable",
                    "https://saydin.app/errors/security-limiter-unavailable",
                    "Request admission is temporarily unavailable.");
            }

            return await next(ctx);
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

    private static IResult SecurityLimiterProblem(
        HttpContext http,
        int status,
        string code,
        string type,
        string title) => Results.Problem(
            title: title,
            statusCode: status,
            type: type,
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
                ["code"] = code,
            });
}
