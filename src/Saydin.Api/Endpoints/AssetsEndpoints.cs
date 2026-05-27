using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Middleware;
using Saydin.Api.Options;
using Saydin.Api.Services;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Endpoints;

public static class AssetsEndpoints
{
    private const string AssetUsageKeyPrefix = "usage:assets:";

    /// <summary>
    /// Asset fiyat aralığı için izin verilen maksimum gün sayısı. Tek istekte
    /// 10 yıldan uzun aralık DoS / pratik olmayan response sebebi sayılır.
    /// </summary>
    private const int MaxPriceRangeDays = 3650;

    private static async Task TryReleaseAsync(
        IDailyLimitGuard limitGuard, string deviceId, int limit, HttpContext httpContext)
    {
        try
        {
            await limitGuard.ReleaseAsync(null, deviceId, AssetUsageKeyPrefix, limit, CancellationToken.None);
        }
        catch (Exception releaseEx)
        {
            // Release hatası orijinal exception'ı maskelemesin (review CodeRabbit feedback).
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AssetsEndpoints));
            logger.LogWarning(releaseEx, "Asset endpoint quota release başarısız: {DeviceId}", deviceId);
        }
    }

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
        IDailyLimitGuard limitGuard,
        IOptions<PlanOptions> plans,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("assets_list");
        var deviceId = httpContext.GetRequiredDeviceId();
        var limit = plans.Value.Free.DailyAssetQueryLimit;

        await limitGuard.TryAcquireAsync(null, deviceId, AssetUsageKeyPrefix, limit, ct);
        try
        {
            var assets = await assetService.GetAllAssetInfoAsync(ct);
            log.WithData(new { assetCount = assets.Count });
            return Results.Ok(new { assets });
        }
        catch
        {
            // Release best-effort: orijinal exception (PriceNotFound, ValidationException vb.)
            // kullanıcıya yansımalı, release hatası onu maskelemesin.
            await TryReleaseAsync(limitGuard, deviceId, limit, httpContext);
            throw;
        }
    }

    private static async Task<IResult> GetPriceAsync(
        string symbol,
        DateOnly date,
        HttpContext httpContext,
        IAssetService assetService,
        IDailyLimitGuard limitGuard,
        IOptions<PlanOptions> plans,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("asset_price");
        var deviceId = httpContext.GetRequiredDeviceId();
        var limit = plans.Value.Free.DailyAssetQueryLimit;

        await limitGuard.TryAcquireAsync(null, deviceId, AssetUsageKeyPrefix, limit, ct);
        try
        {
            var price = await assetService.GetPriceAsync(symbol, date, ct);

            log.WithData(new
            {
                assetSymbol = symbol,
                date = date.ToString("yyyy-MM-dd")
            });
            return Results.Ok(price);
        }
        catch
        {
            // Release best-effort: orijinal exception (PriceNotFound, ValidationException vb.)
            // kullanıcıya yansımalı, release hatası onu maskelemesin.
            await TryReleaseAsync(limitGuard, deviceId, limit, httpContext);
            throw;
        }
    }

    private static async Task<IResult> GetPriceRangeAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        HttpContext httpContext,
        IAssetService assetService,
        IDailyLimitGuard limitGuard,
        IOptions<PlanOptions> plans,
        IStringLocalizer<ErrorMessages> localizer,
        CancellationToken ct,
        string interval = "daily")
    {
        // Tarih aralığı sınırı: keyfi geniş aralıkla DB/Redis cache'i şişirme riskini önler.
        // ValidationException → 400 + ProblemDetails (ValidationExceptionHandler tarafından).
        if (from > to)
            throw new ValidationException(localizer["BuyDateAfterSellDate"], field: nameof(from));

        if (to.DayNumber - from.DayNumber > MaxPriceRangeDays)
            throw new ValidationException(
                string.Format(localizer["PriceRangeTooWideDetail"], MaxPriceRangeDays),
                field: "dateRange");

        var log = httpContext.GetOrCreateActivityLog("asset_price_range");
        var deviceId = httpContext.GetRequiredDeviceId();
        var limit = plans.Value.Free.DailyAssetQueryLimit;

        await limitGuard.TryAcquireAsync(null, deviceId, AssetUsageKeyPrefix, limit, ct);
        try
        {
            var points = await assetService.GetPriceRangeAsync(symbol, from, to, interval, ct);

            log.WithData(new
            {
                assetSymbol = symbol,
                from = from.ToString("yyyy-MM-dd"),
                to = to.ToString("yyyy-MM-dd"),
                interval,
                pointCount = points.Count
            });
            return Results.Ok(new { symbol, interval, pricePoints = points });
        }
        catch
        {
            // Release best-effort: orijinal exception (PriceNotFound, ValidationException vb.)
            // kullanıcıya yansımalı, release hatası onu maskelemesin.
            await TryReleaseAsync(limitGuard, deviceId, limit, httpContext);
            throw;
        }
    }
}
