using Saydin.Api.Helpers;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class AssetsEndpoints
{
    public static IEndpointRouteBuilder MapAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/assets")
            .WithTags("Assets");

        group.MapGet("/", GetAllAsync)
            .WithName("GetAssets")
            .WithSummary("Desteklenen tüm asset'leri listeler");

        group.MapGet("/{symbol}/price/{date}", GetPriceAsync)
            .WithName("GetAssetPrice")
            .WithSummary("Belirli tarihte fiyat döner");

        group.MapGet("/{symbol}/price-range", GetPriceRangeAsync)
            .WithName("GetAssetPriceRange")
            .WithSummary("Tarih aralığında fiyat serisi döner");

        return app;
    }

    private static async Task<IResult> GetAllAsync(
        HttpContext httpContext,
        IAssetService assetService,
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>()).WithAction("assets_list");

        var assets = await assetService.GetAllAssetInfoAsync(ct);

        log.WithData(new { assetCount = assets.Count })
           .Send(activityLogger);

        return Results.Ok(new { assets });
    }

    private static async Task<IResult> GetPriceAsync(
        string symbol,
        DateOnly date,
        HttpContext httpContext,
        IAssetService assetService,
        IActivityLogger activityLogger,
        CancellationToken ct)
    {
        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>()).WithAction("asset_price");

        var price = await assetService.GetPriceAsync(symbol, date, ct);

        log.WithData(new
        {
            assetSymbol = symbol,
            date = date.ToString("yyyy-MM-dd")
        }).Send(activityLogger);

        return Results.Ok(price);
    }

    private static async Task<IResult> GetPriceRangeAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        HttpContext httpContext,
        IAssetService assetService,
        IActivityLogger activityLogger,
        CancellationToken ct,
        string interval = "daily")
    {
        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>()).WithAction("asset_price_range");

        var points = await assetService.GetPriceRangeAsync(symbol, from, to, interval, ct);

        log.WithData(new
        {
            assetSymbol = symbol,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            interval,
            pointCount = points.Count
        }).Send(activityLogger);

        return Results.Ok(new { symbol, interval, pricePoints = points });
    }
}
