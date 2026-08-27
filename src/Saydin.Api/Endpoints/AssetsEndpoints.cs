using Microsoft.Extensions.Localization;
using Saydin.Api.Middleware;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Constants;
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
        IDailyLimitGuard limitGuard, QuotaLease lease, HttpContext httpContext)
    {
        try
        {
            await limitGuard.ReleaseAsync(lease, CancellationToken.None);
        }
        catch (Exception releaseEx)
        {
            // Release hatası orijinal exception'ı maskelemesin (review CodeRabbit feedback).
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AssetsEndpoints));
            logger.LogWarning(releaseEx, "Asset endpoint quota release başarısız");
        }
    }

    public static IEndpointRouteBuilder MapAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/assets")
            .WithTags("Assets");

        // All asset endpoints require the server-issued installation credential;
        // anonymous enumeration and quota bypass are rejected before handler code.
        // F2.1-6: MapGet("") trailing-slash bağımsız.
        // APIR-016: anonim `object` wrapper kalktı — typed AssetListResponse / PriceRangeResponse.
        group.MapGet("", GetAllAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential()
            .WithName("GetAssets")
            .WithSummary("Desteklenen tüm asset'leri listeler")
            .Produces<AssetListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // F7 follow-up: GetPriceAsync da domain entity yerine PricePointResponse döner.
        group.MapGet("/{symbol}/price/{date}", GetPriceAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential()
            .WithName("GetAssetPrice")
            .WithSummary("Belirli tarihte fiyat döner")
            .Produces<PricePointResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/{symbol}/price-range", GetPriceRangeAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireInstallationCredential()
            .WithName("GetAssetPriceRange")
            .WithSummary("Tarih aralığında fiyat serisi döner")
            .Produces<PriceRangeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> GetAllAsync(
        HttpContext httpContext,
        IAssetService assetService,
        IDailyLimitGuard limitGuard,
        IPlanLimitResolver planLimits,
        IInstallationPrincipalContext principalContext,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("assets_list");
        var usageIdentity = principalContext.PrincipalId.ToString("N");
        var limit = await planLimits.ResolveDailyAssetQueryLimitAsync(ct);

        var lease = await limitGuard.TryAcquireAsync(
            null, usageIdentity, AssetUsageKeyPrefix, limit, ct);
        try
        {
            var assets = await assetService.GetAllAssetInfoAsync(ct);
            log.WithData(new { assetCount = assets.Count });
            return Results.Ok(new AssetListResponse(assets));
        }
        catch
        {
            // Release best-effort: orijinal exception (PriceNotFound, ValidationException vb.)
            // kullanıcıya yansımalı, release hatası onu maskelemesin.
            await TryReleaseAsync(limitGuard, lease, httpContext);
            throw;
        }
    }

    private static async Task<IResult> GetPriceAsync(
        string symbol,
        DateOnly date,
        HttpContext httpContext,
        IAssetService assetService,
        IDailyLimitGuard limitGuard,
        IPlanLimitResolver planLimits,
        IInstallationPrincipalContext principalContext,
        CancellationToken ct)
    {
        var log = httpContext.GetOrCreateActivityLog("asset_price");
        var usageIdentity = principalContext.PrincipalId.ToString("N");
        var limit = await planLimits.ResolveDailyAssetQueryLimitAsync(ct);

        var lease = await limitGuard.TryAcquireAsync(
            null, usageIdentity, AssetUsageKeyPrefix, limit, ct);
        try
        {
            var price = await assetService.GetPriceAsync(symbol, date, ct);

            log.WithData(new
            {
                assetSymbol = symbol,
                date = date.ToString("yyyy-MM-dd")
            });
            // F7 follow-up: domain `PricePoint` sızıntısı kalkar — public DTO map.
            // AssetId / Asset navigation alanları response'a yansımaz.
            var response = new PricePointResponse(price.PriceDate, price.Close,
                price.Open, price.High, price.Low, price.Volume,
                AuthorityDataResponseFactory.Exact(FinalObservationAuthority.ToValue(price)));
            return Results.Ok(response);
        }
        catch
        {
            // Release best-effort: orijinal exception (PriceNotFound, ValidationException vb.)
            // kullanıcıya yansımalı, release hatası onu maskelemesin.
            await TryReleaseAsync(limitGuard, lease, httpContext);
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
        IPlanLimitResolver planLimits,
        IInstallationPrincipalContext principalContext,
        IStringLocalizer<ErrorMessages> localizer,
        CancellationToken ct,
        string interval = PriceIntervals.Daily)
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
        var usageIdentity = principalContext.PrincipalId.ToString("N");
        var limit = await planLimits.ResolveDailyAssetQueryLimitAsync(ct);

        var lease = await limitGuard.TryAcquireAsync(
            null, usageIdentity, AssetUsageKeyPrefix, limit, ct);
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
            // F7 follow-up: domain `PricePoint` sızıntısı kalkar — public DTO map.
            var pricePoints = points
                .Select(p => new PricePointResponse(
                    p.PriceDate, p.Close, p.Open, p.High, p.Low, p.Volume,
                    AuthorityDataResponseFactory.Exact(FinalObservationAuthority.ToValue(p))))
                .ToList();
            return Results.Ok(new PriceRangeResponse(symbol, interval, pricePoints));
        }
        catch
        {
            // Release best-effort: orijinal exception (PriceNotFound, ValidationException vb.)
            // kullanıcıya yansımalı, release hatası onu maskelemesin.
            await TryReleaseAsync(limitGuard, lease, httpContext);
            throw;
        }
    }
}
