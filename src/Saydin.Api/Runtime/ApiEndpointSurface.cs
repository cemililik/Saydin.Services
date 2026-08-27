using Microsoft.AspNetCore.Routing.Matching;

namespace Saydin.Api.Runtime;

public enum ApiEndpointSurface
{
    PublicProduct,
    PublicLiveness,
    Management,
}

public sealed record ApiEndpointSurfaceMetadata(ApiEndpointSurface Surface);

/// <summary>
/// Removes route candidates whose declared surface does not match the accepted local
/// listener. This runs during endpoint selection, independently of path spelling and
/// before the boundary middleware's defense-in-depth response.
/// </summary>
public sealed class ApiPortEndpointSelectorPolicy(
    ApiRuntimeContract runtime,
    IHostEnvironment environment) : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 10_000;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) =>
        endpoints.Any(endpoint => endpoint.Metadata.GetMetadata<ApiEndpointSurfaceMetadata>() is not null);

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (!candidates.IsValidCandidate(index))
                continue;
            var metadata = candidates[index].Endpoint.Metadata
                .GetMetadata<ApiEndpointSurfaceMetadata>();
            if (metadata is not null && !IsAccepted(httpContext, metadata.Surface))
                candidates.SetValidity(index, false);
        }

        return Task.CompletedTask;
    }

    private bool IsAccepted(HttpContext context, ApiEndpointSurface surface)
    {
        var port = context.Connection.LocalPort;
        var testServerPublic = port == 0 && !environment.IsProduction();
        return surface switch
        {
            ApiEndpointSurface.PublicProduct or ApiEndpointSurface.PublicLiveness =>
                port == runtime.PublicPort || testServerPublic,
            ApiEndpointSurface.Management => port == runtime.ManagementPort,
            _ => false,
        };
    }
}
