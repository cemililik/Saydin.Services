using Saydin.Api.Models.Requests;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class DcaEndpoints
{
    public static IEndpointRouteBuilder MapDcaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/what-if")
            .WithTags("WhatIf");

        group.MapPost("/dca", CalculateDcaAsync)
            .WithName("CalculateDca")
            .WithSummary("DCA (Dollar-Cost Averaging) hesabı yapar")
            .RequireDeviceId();

        return app;
    }

    private static async Task<IResult> CalculateDcaAsync(
        HttpContext httpContext,
        DcaRequest request,
        IDcaCalculator calculator,
        CancellationToken ct)
    {
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");
        var result = await calculator.CalculateAsync(deviceId, request, ct);
        return Results.Ok(result);
    }
}
