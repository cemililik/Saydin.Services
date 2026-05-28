using Saydin.Api.Middleware;
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
            .Produces<Models.Responses.DcaResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireDeviceId();

        return app;
    }

    private static async Task<IResult> CalculateDcaAsync(
        HttpContext httpContext,
        DcaRequest request,
        IDcaCalculator calculator,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("what_if_dca");
        var deviceId = httpContext.GetRequiredDeviceId();

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
        });

        return Results.Ok(result);
    }
}
