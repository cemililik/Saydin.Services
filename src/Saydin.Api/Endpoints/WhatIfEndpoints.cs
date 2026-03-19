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

        group.MapPost("/compare", CompareAsync)
            .WithName("CompareWhatIf")
            .WithSummary("Birden fazla varlık arasında ya-alsaydım karşılaştırması yapar (2-5 sembol)")
            .RequireDeviceId();

        group.MapPost("/reverse", ReverseCalculateAsync)
            .WithName("ReverseCalculateWhatIf")
            .WithSummary("Ters hesaplama: hedef tutardan gereken yatırımı hesaplar")
            .RequireDeviceId();

        return app;
    }

    private static async Task<IResult> CalculateAsync(
        HttpContext httpContext,
        WhatIfRequest request,
        IWhatIfCalculator calculator,
        CancellationToken ct)
    {
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");
        var result = await calculator.CalculateAsync(deviceId, request, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> CompareAsync(
        HttpContext httpContext,
        CompareRequest request,
        IWhatIfCalculator calculator,
        CancellationToken ct)
    {
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");
        var result = await calculator.CompareAsync(deviceId, request, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> ReverseCalculateAsync(
        HttpContext httpContext,
        ReverseWhatIfRequest request,
        IWhatIfCalculator calculator,
        CancellationToken ct)
    {
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");
        var result = await calculator.CalculateReverseAsync(deviceId, request, ct);
        return Results.Ok(result);
    }
}
