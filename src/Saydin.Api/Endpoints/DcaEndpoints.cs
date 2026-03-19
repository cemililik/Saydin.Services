using Saydin.Api.Helpers;
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
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext).WithAction("what_if_dca");
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");

        var result = await calculator.CalculateAsync(deviceId, request, ct);

        log.WithData(new
        {
            request.AssetSymbol,
            startDate = request.StartDate.ToString("yyyy-MM-dd"),
            endDate = request.EndDate?.ToString("yyyy-MM-dd"),
            request.PeriodicAmount,
            request.Period,
            request.AmountType,
            request.IncludeInflation,
            result = new
            {
                result.TotalInvestedTry,
                result.CurrentValueTry,
                result.ProfitLossPercent,
                result.ProfitLossTry,
                result.AverageCostPerUnit,
                result.TotalPurchases,
                result.RealProfitLossPercent,
            }
        }).Send(activityLogger);

        return Results.Ok(result);
    }
}
