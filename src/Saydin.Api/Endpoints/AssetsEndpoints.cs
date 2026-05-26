using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Helpers;
using Saydin.Api.Options;
using Saydin.Api.Services;

namespace Saydin.Api.Endpoints;

public static class AssetsEndpoints
{
    private const string AssetUsageKeyPrefix = "usage:assets:";

    /// <summary>
    /// Asset fiyat aralığı için izin verilen maksimum gün sayısı. Tek istekte
    /// 10 yıldan uzun aralık DoS / pratik olmayan response sebebi sayılır.
    /// </summary>
    private const int MaxPriceRangeDays = 3650;

    public static IEndpointRouteBuilder MapAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/assets")
            .WithTags("Assets");

        // Tüm asset endpoint'leri DeviceId ister: anonim enumeration + DB/Redis DoS
        // riskini kapatır. CLAUDE.md "Daily limit / device kontrolü" prensibine uyumlu.
        group.MapGet("/", GetAllAsync)
            .RequireDeviceId()
            .WithName("GetAssets")
            .WithSummary("Desteklenen tüm asset'leri listeler");

        group.MapGet("/{symbol}/price/{date}", GetPriceAsync)
            .RequireDeviceId()
            .WithName("GetAssetPrice")
            .WithSummary("Belirli tarihte fiyat döner");

        group.MapGet("/{symbol}/price-range", GetPriceRangeAsync)
            .RequireDeviceId()
            .WithName("GetAssetPriceRange")
            .WithSummary("Tarih aralığında fiyat serisi döner");

        return app;
    }

    private static async Task<IResult> GetAllAsync(
        HttpContext httpContext,
        IAssetService assetService,
        IActivityLogger activityLogger,
        IDailyLimitGuard limitGuard,
        IOptions<PlanOptions> plans,
        CancellationToken ct)
    {
        var deviceId = GetDeviceId(httpContext);

        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>())
            .WithAction("assets_list");

        // Asset endpoint'leri tier-bağımsız ortak limit kullanır (anonim DoS koruması).
        // user=null geçilince guard deviceId üzerinden sayaç tutar ve premium muafiyeti uygulamaz.
        var assetLimit = plans.Value.Free.DailyAssetQueryLimit;
        await limitGuard.CheckAsync(null, deviceId, AssetUsageKeyPrefix, assetLimit, ct);

        var assets = await assetService.GetAllAssetInfoAsync(ct);

        await limitGuard.IncrementAsync(null, deviceId, AssetUsageKeyPrefix, assetLimit, ct);

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
        IDailyLimitGuard limitGuard,
        IOptions<PlanOptions> plans,
        CancellationToken ct)
    {
        var deviceId = GetDeviceId(httpContext);

        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>())
            .WithAction("asset_price");

        var assetLimit = plans.Value.Free.DailyAssetQueryLimit;
        await limitGuard.CheckAsync(null, deviceId, AssetUsageKeyPrefix, assetLimit, ct);

        var price = await assetService.GetPriceAsync(symbol, date, ct);

        await limitGuard.IncrementAsync(null, deviceId, AssetUsageKeyPrefix, assetLimit, ct);

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
        IDailyLimitGuard limitGuard,
        IOptions<PlanOptions> plans,
        IStringLocalizer<ErrorMessages> localizer,
        CancellationToken ct,
        string interval = "daily")
    {
        var deviceId = GetDeviceId(httpContext);

        // Tarih aralığı sınırı: keyfi geniş aralıkla DB/Redis cache'i şişirme riskini önler.
        if (from > to)
        {
            return Results.Problem(
                title: localizer["BuyDateAfterSellDate"],
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://saydin.app/errors/invalid-date-range");
        }
        if (to.DayNumber - from.DayNumber > MaxPriceRangeDays)
        {
            return Results.Problem(
                title: localizer["PriceRangeTooWide"],
                detail: string.Format(localizer["PriceRangeTooWideDetail"], MaxPriceRangeDays),
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://saydin.app/errors/price-range-too-wide");
        }

        var log = new ActivityLogBuilder(httpContext, httpContext.RequestServices.GetService<IGeoIpResolver>())
            .WithAction("asset_price_range");

        var assetLimit = plans.Value.Free.DailyAssetQueryLimit;
        await limitGuard.CheckAsync(null, deviceId, AssetUsageKeyPrefix, assetLimit, ct);

        var points = await assetService.GetPriceRangeAsync(symbol, from, to, interval, ct);

        await limitGuard.IncrementAsync(null, deviceId, AssetUsageKeyPrefix, assetLimit, ct);

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

    private static string GetDeviceId(HttpContext httpContext) =>
        httpContext.Items[EndpointExtensions.DeviceIdItemKey] as string
        ?? throw new InvalidOperationException(
            "DeviceId, RequireDeviceId filter'ı atlanarak ulaşıldı.");
}
