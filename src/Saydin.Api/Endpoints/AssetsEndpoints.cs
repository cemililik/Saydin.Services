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
        IAssetService assetService,
        CancellationToken ct)
    {
        var assets = await assetService.GetAllAsync(ct);
        return Results.Ok(new { assets });
    }

    private static async Task<IResult> GetPriceAsync(
        string symbol,
        DateOnly date,
        IAssetService assetService,
        CancellationToken ct)
    {
        var price = await assetService.GetPriceAsync(symbol, date, ct);
        return Results.Ok(price);
    }

    private static async Task<IResult> GetPriceRangeAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        string interval,
        IAssetService assetService,
        CancellationToken ct)
    {
        var points = await assetService.GetPriceRangeAsync(symbol, from, to, interval, ct);
        return Results.Ok(new { symbol, interval, points });
    }
}
