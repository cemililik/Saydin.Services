using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Saydin.Api.Middleware;
using Saydin.Api.Services;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Middleware;

public sealed class ActivityLogMiddlewareTests
{
    [Theory]
    [InlineData(400, "request_invalid")]
    [InlineData(401, "authentication_failed")]
    [InlineData(403, "request_forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(413, "payload_too_large")]
    [InlineData(415, "unsupported_media_type")]
    [InlineData(422, "unprocessable_entity")]
    [InlineData(429, "rate_limited")]
    [InlineData(502, "bad_gateway")]
    [InlineData(503, "service_unavailable")]
    [InlineData(500, "internal_error")]
    public async Task ProductFailuresBeforeHandler_AreAuditedWithStableOutcome(
        int status, string errorCode)
    {
        var logger = Substitute.For<IActivityLogger>();
        var sut = new ActivityLogMiddleware(
            logger, NullLogger<ActivityLogMiddleware>.Instance);
        var context = ProductContext("CalculateWhatIf");

        await sut.InvokeAsync(context, request =>
        {
            request.Response.StatusCode = status;
            return Task.CompletedTask;
        });

        logger.Received(1).Log(Arg.Is<ActivityLog>(entry =>
            entry.Action == "what_if_calculate"
            && entry.StatusCode == status
            && entry.ErrorCode == errorCode
            && entry.IpAddress == null
            && entry.Data == null));
    }

    [Theory]
    [InlineData(ApiPortRequestKind.PublicLiveness)]
    [InlineData(ApiPortRequestKind.Management)]
    public async Task HealthAndMetrics_AreExcluded(ApiPortRequestKind kind)
    {
        var logger = Substitute.For<IActivityLogger>();
        var sut = new ActivityLogMiddleware(
            logger, NullLogger<ActivityLogMiddleware>.Instance);
        var context = ProductContext("GetAssets");
        context.Items[ApiPortBoundary.RequestKindItemKey] = kind;

        await sut.InvokeAsync(context, _ => Task.CompletedTask);

        logger.DidNotReceiveWithAnyArgs().Log(default!);
    }

    [Theory]
    [InlineData("RegisterInstallation", ActivityActions.InstallationRegister)]
    [InlineData("BeginInstallationRotation", ActivityActions.InstallationRotationBegin)]
    [InlineData("CommitInstallationRotation", ActivityActions.InstallationRotationCommit)]
    [InlineData("RevokeInstallation", ActivityActions.InstallationRevoke)]
    public async Task InstallationFailures_AreAuditedWithoutCredentialMaterial(
        string endpointName, string expectedAction)
    {
        var principalId = Guid.Parse("5f52f467-e470-43db-bb9b-2fc204a9b892");
        var resolved = false;
        var principal = Substitute.For<IInstallationPrincipalContext>();
        principal.IsResolved.Returns(_ => resolved);
        principal.PrincipalId.Returns(principalId);
        var logger = Substitute.For<IActivityLogger>();
        var sut = new ActivityLogMiddleware(
            logger, NullLogger<ActivityLogMiddleware>.Instance);
        var context = ProductContext(endpointName, principal);
        context.Request.Headers.Authorization =
            "Bearer v1.secret-material-that-must-never-be-audited";
        context.Request.QueryString = new QueryString(
            "?rotationId=f00dbabe-0000-4000-8000-000000000001");

        await sut.InvokeAsync(context, request =>
        {
            resolved = true;
            request.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });

        logger.Received(1).Log(Arg.Is<ActivityLog>(entry =>
            entry.Action == expectedAction
            && entry.StatusCode == StatusCodes.Status401Unauthorized
            && entry.UserId == principalId
            && entry.Data == null
            && entry.DeviceId == "unknown"));
    }

    private static DefaultHttpContext ProductContext(
        string endpointName,
        IInstallationPrincipalContext? principal = null)
    {
        var serviceCollection = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(
                new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero)));
        if (principal is not null)
            serviceCollection.AddSingleton(principal);
        var services = serviceCollection.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Items[ApiPortBoundary.RequestKindItemKey] = ApiPortRequestKind.PublicProduct;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new TestEndpointNameMetadata(endpointName)),
            endpointName));
        return context;
    }

    private sealed record TestEndpointNameMetadata(string EndpointName) : IEndpointNameMetadata;
}
