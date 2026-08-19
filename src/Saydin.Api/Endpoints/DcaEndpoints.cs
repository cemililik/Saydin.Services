using Saydin.Api.Helpers;
using Saydin.Api.Middleware;
using Saydin.Api.Models.Requests;
using Saydin.Api.Services;
using Saydin.Shared.Constants;

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
            // APIR-003: 403 — DCA feature kapalı veya history sınırı aşıldı.
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential();

        return app;
    }

    private static async Task<IResult> CalculateDcaAsync(
        HttpContext httpContext,
        DcaRequest request,
        IDcaCalculator calculator,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("what_if_dca");

        var result = await calculator.CalculateAsync(request, ct);

        // API-06: ham periyodik tutar bucket'lanır; exact TL/yüzde sonuçlar
        // yalnız düşük kardinaliteli outcome'a indirgenir.
        log.WithData(new
        {
            request.AssetSymbol,
            startDate = request.StartDate.ToString("yyyy-MM-dd"),
            endDate = request.EndDate?.ToString("yyyy-MM-dd"),
            amountBucket = AmountBucket.Coarse(request.PeriodicAmount),
            request.Period,
            request.AmountType,
            request.IncludeInflation,
            result = new
            {
                result.TotalPurchases,
                outcome = TelemetryOutcome.From(result.ProfitLossTry),
                realOutcome = TelemetryOutcome.From(result.RealProfitLossPercent),
            }
        });

        return Results.Ok(result);
    }
}
