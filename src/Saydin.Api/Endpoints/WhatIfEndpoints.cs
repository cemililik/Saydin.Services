using Saydin.Api.Helpers;
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
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>()).WithAction("what_if_calculate");
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");

        var result = await calculator.CalculateAsync(deviceId, request, ct);

        log.WithData(new
        {
            request.AssetSymbol,
            buyDate = request.BuyDate.ToString("yyyy-MM-dd"),
            sellDate = request.SellDate?.ToString("yyyy-MM-dd"),
            request.Amount,
            request.AmountType,
            request.IncludeInflation,
            result = new
            {
                result.ProfitLossPercent,
                result.ProfitLossTry,
                result.IsProfit,
                result.RealProfitLossPercent,
                actualBuyDate = result.ActualBuyDate?.ToString("yyyy-MM-dd"),
                actualSellDate = result.ActualSellDate?.ToString("yyyy-MM-dd"),
            }
        }).Send(activityLogger);

        return Results.Ok(result);
    }

    private static async Task<IResult> CompareAsync(
        HttpContext httpContext,
        CompareRequest request,
        IWhatIfCalculator calculator,
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>()).WithAction("what_if_compare");
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");

        var result = await calculator.CompareAsync(deviceId, request, ct);

        log.WithData(new
        {
            request.AssetSymbols,
            buyDate = request.BuyDate.ToString("yyyy-MM-dd"),
            sellDate = request.SellDate?.ToString("yyyy-MM-dd"),
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
        }).Send(activityLogger);

        return Results.Ok(result);
    }

    private static async Task<IResult> ReverseCalculateAsync(
        HttpContext httpContext,
        ReverseWhatIfRequest request,
        IWhatIfCalculator calculator,
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>()).WithAction("what_if_reverse");
        var deviceId = httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
            ?? throw new InvalidOperationException("DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");

        var result = await calculator.CalculateReverseAsync(deviceId, request, ct);

        log.WithData(new
        {
            request.AssetSymbol,
            buyDate = request.BuyDate.ToString("yyyy-MM-dd"),
            sellDate = request.SellDate?.ToString("yyyy-MM-dd"),
            request.TargetAmount,
            request.TargetAmountType,
            request.IncludeInflation,
            result = new
            {
                result.RequiredInvestmentTry,
                result.ProfitLossPercent,
                result.IsProfit,
                result.RealProfitLossPercent,
                actualBuyDate = result.ActualBuyDate?.ToString("yyyy-MM-dd"),
                actualSellDate = result.ActualSellDate?.ToString("yyyy-MM-dd"),
            }
        }).Send(activityLogger);

        return Results.Ok(result);
    }
}
