using Saydin.Api.Models.Requests;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class WhatIfEndpoints
{
    public static IEndpointRouteBuilder MapWhatIfEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/what-if")
            .WithTags("WhatIf");

        group.MapPost("/calculate", CalculateAsync)
            .WithName("CalculateWhatIf")
            .WithSummary("Ya-alsaydım hesabı yapar")
            .RequireDeviceId();

        return app;
    }

    private static async Task<IResult> CalculateAsync(
        HttpContext httpContext,
        WhatIfRequest request,
        IWhatIfCalculator calculator,
        CancellationToken ct)
    {
        // Filter tarafından validate edilip Items'a yazıldı
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");
        var result = await calculator.CalculateAsync(deviceId, request, ct);
        return Results.Ok(result);
    }
}
