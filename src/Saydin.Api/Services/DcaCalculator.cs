using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

public sealed class DcaCalculator(
    IAssetService assetService,
    ISavedScenarioRepository scenarioRepository,
    IInflationRepository inflationRepository,
    IDailyLimitGuard dailyLimitGuard,
    IRedisCacheHelper cache,
    IAssetNameLocalizer assetNameLocalizer,
    IOptions<PlanOptions> options,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<DcaCalculator> logger) : IDcaCalculator
{
    private const string DcaUsageKeyPrefix   = "usage:dca:";
    private const int    MaxChartPoints      = 60;
    // DoS koruması: 10 yıl haftalık ≈ 520 alma noktası. Bunun üzeri ya yanlış
    // tarih aralığı ya da malicious — anonim free kullanıcının tek istekle
    // 6500 nokta üretmesini engelle (review M-16).
    private const int    MaxPurchasePoints   = 600;

    public async Task<DcaResponse> CalculateAsync(string deviceId, DcaRequest request, CancellationToken ct)
    {
        // P1R-003: domain ValidationException ile guard — handler'ın ArgumentException
        // catch'i altyapı/framework hatalarını yutmasın diye request/deviceId null check'i
        // burada explicit yapılır.
        if (request is null)
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], "request"), field: "request");
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ValidationException(localizer["DeviceIdRequiredDetail"], field: "deviceId");

        EnsureRequired(request.AssetSymbol, nameof(request.AssetSymbol));
        EnsureRequired(request.AmountType, nameof(request.AmountType));
        EnsureRequired(request.Period, nameof(request.Period));

        // F1.9-4 ([C-F-14]): Negatif / sıfır periyodik tutar geçersiz — pozitif zorunlu.
        if (request.PeriodicAmount <= 0m)
            throw new ValidationException(
                localizer["AmountMustBePositive"], field: nameof(request.PeriodicAmount));

        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        var features = options.Value.GetTierOptions(user?.Tier).Features;

        if (!features.Dca)
            throw new FeatureDisabledException(localizer["FeatureDisabled"], featureKey: "dca");

        if (request.IncludeInflation && !features.InflationAdjustment)
            throw new FeatureDisabledException(localizer["FeatureDisabled"], featureKey: "inflation");

        await dailyLimitGuard.TryAcquireAsync(user, deviceId, DcaUsageKeyPrefix, ct: ct);

        try
        {
            return await CalculateCoreAsync(request, ct);
        }
        catch
        {
            await TryReleaseAsync(user, deviceId);
            throw;
        }
    }

    private async Task TryReleaseAsync(User? user, string deviceId)
    {
        try
        {
            await dailyLimitGuard.ReleaseAsync(user, deviceId, DcaUsageKeyPrefix, ct: CancellationToken.None);
        }
        catch (Exception releaseEx)
        {
            // Release best-effort: orijinal calculator exception'ı maskelemesin.
            logger.LogWarning(releaseEx, "Daily limit release başarısız (DCA)");
        }
    }

    private async Task<DcaResponse> CalculateCoreAsync(DcaRequest request, CancellationToken ct)
    {
        var symbol     = request.AssetSymbol.ToUpperInvariant();
        var endDate    = request.EndDate
            ?? await assetService.GetLatestPriceDateAsync(symbol, ct);
        var amountType = request.AmountType.ToLowerInvariant();
        var period     = request.Period.ToLowerInvariant();

        if (request.StartDate > endDate)
            throw new ValidationException(localizer["BuyDateAfterSellDate"], field: nameof(request.StartDate));

        if (period is not ("weekly" or "monthly"))
            throw new ValidationException(
                string.Format(localizer["InvalidPeriod"], request.Period),
                field: nameof(request.Period));

        if (amountType is not "try")
            throw new ValidationException(
                string.Format(localizer["InvalidDcaAmountType"], request.AmountType),
                field: nameof(request.AmountType));

        // ── Cache kontrolü ──────────────────────────────────────────────────
        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var amountStr = request.PeriodicAmount.ToString("G", CultureInfo.InvariantCulture);
        var cacheKey = $"dca:v1:{symbol}:{request.StartDate:yyyy-MM-dd}:{endDate:yyyy-MM-dd}:{amountStr}:{period}:{amountType}{inflationSuffix}:{lang}";

        var cached = await cache.TryGetAsync<DcaResponse>(cacheKey, ct);
        if (cached is not null)
            return cached;

        // ── Asset bilgisi ───────────────────────────────────────────────────
        var asset = await assetService.GetBySymbolAsync(symbol, ct)
            ?? throw new AssetNotFoundException(symbol);

        // ── Alım tarihlerini oluştur ────────────────────────────────────────
        var purchaseDates = GeneratePurchaseDates(request.StartDate, endDate, period);
        if (purchaseDates.Count > MaxPurchasePoints)
            throw new ValidationException(
                string.Format(localizer["DcaRangeTooWide"], MaxPurchasePoints),
                field: nameof(request.StartDate));

        // ── Her alım tarihi için hesaplama ──────────────────────────────────
        var purchases      = new List<DcaPurchase>(purchaseDates.Count);
        var cumulativeUnits = 0m;
        var cumulativeCost  = 0m;

        foreach (var purchaseDate in purchaseDates)
        {
            var pricePoint    = await assetService.GetNearestPriceAsync(symbol, purchaseDate, ct);
            var price         = pricePoint.Close;
            if (price == 0)
                throw new PriceNotFoundException(symbol, purchaseDate);

            var unitsAcquired = Math.Round(request.PeriodicAmount / price, 6, MidpointRounding.AwayFromZero);

            cumulativeUnits += unitsAcquired;
            cumulativeCost  += request.PeriodicAmount;

            var cumulativeValue = Math.Round(cumulativeUnits * price, 2, MidpointRounding.AwayFromZero);

            // F1.3-4 ([C-B-Dca-3]): Hafta sonu / tatil clip ile aynı PriceDate'e iki
            // alım düşerse ikinci kaydı önceki ile birleştir — purchases listesinde
            // her satır benzersiz bir piyasa günüdür. Cumulative değerler en güncel
            // satıra yazılır (toplam doğru kalır).
            if (purchases.Count > 0 && purchases[^1].Date == pricePoint.PriceDate)
            {
                var prev = purchases[^1];
                purchases[^1] = prev with
                {
                    UnitsAcquired      = Math.Round(prev.UnitsAcquired + unitsAcquired, 6, MidpointRounding.AwayFromZero),
                    CumulativeUnits    = Math.Round(cumulativeUnits, 6, MidpointRounding.AwayFromZero),
                    CumulativeCostTry  = Math.Round(cumulativeCost, 2, MidpointRounding.AwayFromZero),
                    CumulativeValueTry = cumulativeValue,
                };
                continue;
            }

            purchases.Add(new DcaPurchase(
                Date:              pricePoint.PriceDate,
                Price:             price,
                UnitsAcquired:     unitsAcquired,
                CumulativeUnits:   Math.Round(cumulativeUnits, 6, MidpointRounding.AwayFromZero),
                CumulativeCostTry: Math.Round(cumulativeCost, 2, MidpointRounding.AwayFromZero),
                CumulativeValueTry: cumulativeValue));
        }

        // ── Güncel değer ve kâr/zarar ───────────────────────────────────────
        var latestPricePoint = await assetService.GetNearestPriceAsync(symbol, endDate, ct);
        var currentUnitPrice = latestPricePoint.Close;
        // Purchase-side zero-price guard ile aynı kontrat — yoksa "0 TL şu an" diye
        // uydurulmuş bir response döner. Terminal fiyatı bulunamadıysa 404.
        if (currentUnitPrice == 0)
            throw new PriceNotFoundException(symbol, endDate);

        var totalUnitsAcquired = Math.Round(cumulativeUnits, 6, MidpointRounding.AwayFromZero);
        var totalInvestedTry   = Math.Round(cumulativeCost, 2, MidpointRounding.AwayFromZero);
        var currentValueTry    = Math.Round(totalUnitsAcquired * currentUnitPrice, 2, MidpointRounding.AwayFromZero);
        var profitLossTry      = currentValueTry - totalInvestedTry;
        var profitLossPercent  = totalInvestedTry == 0
            ? 0m
            : Math.Round(profitLossTry / totalInvestedTry * 100, 2, MidpointRounding.AwayFromZero);

        var averageCostPerUnit = totalUnitsAcquired == 0
            ? 0m
            : Math.Round(totalInvestedTry / totalUnitsAcquired, 2, MidpointRounding.AwayFromZero);

        // ── Enflasyon düzeltmesi ────────────────────────────────────────────
        decimal?  cumulativeInflationPercent = null;
        decimal?  realProfitLossPercent      = null;
        DateOnly? inflationDataAsOf          = null;

        if (request.IncludeInflation)
        {
            try
            {
                var (buyIdx, _, sellIdx, sellIdxDate) =
                    await inflationRepository.GetIndexValuesAsync(request.StartDate, endDate, ct);

                if (buyIdx is not null && sellIdx is not null && buyIdx != 0)
                {
                    cumulativeInflationPercent = Math.Round(
                        (sellIdx.Value / buyIdx.Value - 1m) * 100, 2, MidpointRounding.AwayFromZero);

                    // Fisher denklemi: reel_getiri = (1 + nominal) / (1 + enflasyon) - 1
                    var nominalFactor   = 1m + profitLossPercent / 100m;
                    var inflationFactor = 1m + cumulativeInflationPercent.Value / 100m;
                    realProfitLossPercent = Math.Round(
                        (nominalFactor / inflationFactor - 1m) * 100, 2, MidpointRounding.AwayFromZero);

                    var expectedSellMonth = new DateOnly(endDate.Year, endDate.Month, 1);
                    if (sellIdxDate.HasValue && sellIdxDate.Value < expectedSellMonth)
                        inflationDataAsOf = sellIdxDate;
                }
                else
                {
                    logger.LogWarning(
                        "Enflasyon endeksi bulunamadı: {StartDate} / {EndDate}", request.StartDate, endDate);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Enflasyon hesabı başarısız, nominal getiri kullanılıyor");
            }
        }

        // ── Chart data (max 60 nokta) ───────────────────────────────────────
        var chartData = SampleChartData(purchases, MaxChartPoints);

        var response = new DcaResponse(
            AssetSymbol:                symbol,
            AssetDisplayName:           assetNameLocalizer.Localize(symbol, asset.DisplayName),
            StartDate:                  request.StartDate,
            EndDate:                    endDate,
            Period:                     period,
            PeriodicAmount:             request.PeriodicAmount,
            TotalPurchases:             purchases.Count,
            TotalInvestedTry:           totalInvestedTry,
            CurrentValueTry:            currentValueTry,
            ProfitLossTry:              profitLossTry,
            ProfitLossPercent:          profitLossPercent,
            IsProfit:                   profitLossTry >= 0,
            AverageCostPerUnit:         averageCostPerUnit,
            TotalUnitsAcquired:         totalUnitsAcquired,
            CurrentUnitPrice:           currentUnitPrice,
            CumulativeInflationPercent: cumulativeInflationPercent,
            RealProfitLossPercent:      realProfitLossPercent,
            InflationDataAsOf:          inflationDataAsOf,
            Purchases:                  purchases,
            ChartData:                  chartData);

        await cache.TrySetAsync(cacheKey, response, TimeSpan.FromHours(1), ct);

        logger.LogInformation(
            "DCA hesaplandı: {Symbol} {StartDate}→{EndDate} {Period} {Amount} → %{ProfitLossPercent} (reel: %{RealProfitLossPercent})",
            symbol, request.StartDate, endDate, period, request.PeriodicAmount,
            profitLossPercent, realProfitLossPercent);

        return response;
    }

    private void EnsureRequired(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], field), field: field);
    }

    private static List<DateOnly> GeneratePurchaseDates(DateOnly startDate, DateOnly endDate, string period)
    {
        var dates = new List<DateOnly>();

        // F1.3-3 ([C-B-Dca-2]): Monthly serilerde `current.AddMonths(1)` kullanmak
        // anchor day kaymasına yol açar (örn. 31 Ocak → 28 Şubat → 28 Mart …).
        // Tüm tarihler `startDate`'e göre indeks-bazlı hesaplanır; `AddMonths` ay
        // sonu clamp'ini hâlâ uygular ama bir sonraki ay startDate'in gününden devam eder.
        if (period == "weekly")
        {
            for (var current = startDate; current <= endDate; current = current.AddDays(7))
                dates.Add(current);
        }
        else
        {
            // Sınırlı iterasyon: AddMonths()'a yalnızca [0, monthsDiff] aralığında çağrı
            // yapılır — `endDate` patolojik biçimde DateOnly.MaxValue'ye yakınsa bile
            // ArgumentOutOfRangeException üretmez.
            var monthsDiff = (endDate.Year - startDate.Year) * 12 + (endDate.Month - startDate.Month);
            if (monthsDiff < 0) monthsDiff = 0;
            for (var i = 0; i <= monthsDiff; i++)
            {
                var candidate = startDate.AddMonths(i);
                if (candidate > endDate) break;
                dates.Add(candidate);
            }
        }

        return dates;
    }

    private static IReadOnlyList<DcaChartPoint> SampleChartData(
        List<DcaPurchase> purchases, int maxPoints)
    {
        if (purchases.Count == 0) return Array.Empty<DcaChartPoint>();

        if (purchases.Count <= maxPoints)
        {
            return purchases
                .Select(p => new DcaChartPoint(p.Date, p.CumulativeCostTry, p.CumulativeValueTry))
                .ToList();
        }

        var result = new List<DcaChartPoint>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
        {
            var idx = Math.Min((int)((double)i * (purchases.Count - 1) / (maxPoints - 1)), purchases.Count - 1);
            var p   = purchases[idx];
            result.Add(new DcaChartPoint(p.Date, p.CumulativeCostTry, p.CumulativeValueTry));
        }

        return result;
    }
}
