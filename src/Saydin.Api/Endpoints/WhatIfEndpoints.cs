using Saydin.Api.Middleware;
using Saydin.Api.Models.Requests;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class WhatIfEndpoints
{
    /// <summary>ISO-8601 tarih formatı; tüm activity log payload'larında tutarlı kullanılır.</summary>
    private const string IsoDate = "yyyy-MM-dd";

    public static IEndpointRouteBuilder MapWhatIfEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/what-if")
            .WithTags("WhatIf");

        // F2.1-4 ([C-A-13], [G-A-03]) + APIR-002/003: Typed Results — OpenAPI şeması
        // her endpoint için explicit dönüş tipi + olası problem kodlarını taşır.
        // 403 (FeatureDisabledException → extended_history / dca / inflation) ve
        // 422 (ScenarioLimitExceededException) eklendi.
        group.MapPost("/calculate", CalculateAsync)
            .WithName("CalculateWhatIf")
            .WithSummary("Ya-alsaydım hesabı yapar")
            .Produces<Models.Responses.WhatIfResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireDeviceId();

        group.MapPost("/compare", CompareAsync)
            .WithName("CompareWhatIf")
            .WithSummary("Birden fazla varlık arasında ya-alsaydım karşılaştırması yapar (2-5 sembol)")
            .Produces<Models.Responses.CompareResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireDeviceId();

        group.MapPost("/reverse", ReverseCalculateAsync)
            .WithName("ReverseCalculateWhatIf")
            .WithSummary("Ters hesaplama: hedef tutardan gereken yatırımı hesaplar")
            .Produces<Models.Responses.ReverseWhatIfResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireDeviceId();

        return app;
    }

    private static async Task<IResult> CalculateAsync(
        HttpContext httpContext,
        WhatIfRequest request,
        IWhatIfCalculator calculator,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("what_if_calculate");
        var deviceId = httpContext.GetRequiredDeviceId();

        var result = await calculator.CalculateAsync(deviceId, request, ct);

        log.WithData(new
        {
            request.AssetSymbol,
            buyDate = request.BuyDate.ToString(IsoDate),
            sellDate = request.SellDate?.ToString(IsoDate),
            request.Amount,
            request.AmountType,
            request.IncludeInflation,
            result = new
            {
                result.ProfitLossPercent,
                result.ProfitLossTry,
                result.IsProfit,
                result.RealProfitLossPercent,
                actualBuyDate = result.ActualBuyDate?.ToString(IsoDate),
                actualSellDate = result.ActualSellDate?.ToString(IsoDate),
            }
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> CompareAsync(
        HttpContext httpContext,
        CompareRequest request,
        IWhatIfCalculator calculator,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("what_if_compare");
        var deviceId = httpContext.GetRequiredDeviceId();

        var result = await calculator.CompareAsync(deviceId, request, ct);

        log.WithData(new
        {
            request.AssetSymbols,
            buyDate = request.BuyDate.ToString(IsoDate),
            sellDate = request.SellDate?.ToString(IsoDate),
            request.Amount,
            request.AmountType,
            request.IncludeInflation,
            result = new
            {
                winner = result.Results.FirstOrDefault()?.Calculation.AssetSymbol,
                rankings = result.Results.Select(r => new
                {
                    r.Rank,
                    symbol = r.Calculation.AssetSymbol,
                    r.Calculation.ProfitLossPercent
                })
            }
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> ReverseCalculateAsync(
        HttpContext httpContext,
        ReverseWhatIfRequest request,
        IWhatIfCalculator calculator,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("what_if_reverse");
        var deviceId = httpContext.GetRequiredDeviceId();

        var result = await calculator.CalculateReverseAsync(deviceId, request, ct);

        log.WithData(new
        {
            request.AssetSymbol,
            buyDate = request.BuyDate.ToString(IsoDate),
            sellDate = request.SellDate?.ToString(IsoDate),
            request.TargetAmount,
            request.TargetAmountType,
            request.IncludeInflation,
            result = new
            {
                result.RequiredInvestmentTry,
                result.ProfitLossPercent,
                result.IsProfit,
                result.RealProfitLossPercent,
                actualBuyDate = result.ActualBuyDate?.ToString(IsoDate),
                actualSellDate = result.ActualSellDate?.ToString(IsoDate),
            }
        });

        return Results.Ok(result);
    }
}
