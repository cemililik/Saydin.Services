using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Helpers;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

public sealed class DcaCalculator(
    IAssetService assetService,
    ISavedScenarioRepository scenarioRepository,
    IInflationRepository inflationRepository,
    IDailyLimitGuard dailyLimitGuard,
    IInstallationPrincipalContext principalContext,
    TimeProvider timeProvider,
    IRedisCacheHelper cache,
    IAssetNameLocalizer assetNameLocalizer,
    IOptions<PlanOptions> options,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<DcaCalculator> logger) : IDcaCalculator
{
    private const string DcaUsageKeyPrefix   = "usage:dca:";
    private const string RealReturnMethodCashflowCpiTerminal = "cashflow_cpi_lkv_terminal_v1";
    private const int    MaxChartPoints      = 60;
    // DoS koruması: 10 yıl haftalık ≈ 520 alma noktası. Bunun üzeri ya yanlış
    // tarih aralığı ya da malicious — anonim free kullanıcının tek istekle
    // 6500 nokta üretmesini engelle (review M-16).
    private const int    MaxPurchasePoints   = 600;

    public Task<DcaResponse> CalculateAsync(DcaRequest request, CancellationToken ct) =>
        CalculationTelemetry.ObserveDcaAsync(
            "dca", () => CalculateObservedAsync(request, ct));

    private async Task<DcaResponse> CalculateObservedAsync(DcaRequest request, CancellationToken ct)
    {
        // P1R-003: domain ValidationException ile guard — handler'ın ArgumentException
        // catch'i altyapı/framework hatalarını yutmasın diye request null check'i explicit.
        if (request is null)
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], "request"), field: "request");

        // Quota keys use only the authenticated server-issued principal UUID.
        var usageIdentity = principalContext.PrincipalId.ToString("N");

        EnsureRequired(request.AssetSymbol, nameof(request.AssetSymbol));
        EnsureRequired(request.AmountType, nameof(request.AmountType));
        EnsureRequired(request.Period, nameof(request.Period));

        // F1.9-4 ([C-F-14]): Negatif / sıfır periyodik tutar geçersiz — pozitif zorunlu.
        if (request.PeriodicAmount <= 0m)
            throw new ValidationException(
                localizer["AmountMustBePositive"], field: nameof(request.PeriodicAmount));

        var user = await scenarioRepository.GetUserByIdAsync(principalContext.PrincipalId, ct)
            ?? throw new InvalidOperationException("Authenticated installation principal is missing.");
        var features = options.Value.GetTierOptions(user.Tier).Features;

        if (!features.Dca)
            throw new FeatureDisabledException(localizer["FeatureDisabled"], featureKey: "dca");

        // F2.2-22 ([G-B-02]): DCA `StartDate` tier'ın PriceHistoryMonths penceresinin
        // dışına çıkamaz. 0 = sınırsız (premium).
        if (features.PriceHistoryMonths > 0)
        {
            var earliestAllowed = BusinessClock.TodayInIstanbul(timeProvider)
                .AddMonths(-features.PriceHistoryMonths);
            if (request.StartDate < earliestAllowed)
                throw new FeatureDisabledException(localizer["FeatureDisabled"], featureKey: "extended_history");
        }

        if (request.IncludeInflation && !features.InflationAdjustment)
            throw new FeatureDisabledException(localizer["FeatureDisabled"], featureKey: "inflation");

        if (request.EndDate is { } requestedEndDate
            && requestedEndDate > BusinessClock.TodayInIstanbul(timeProvider))
        {
            throw new ValidationException(
                localizer["CalculationEndDateCannotBeInFuture"],
                field: nameof(request.EndDate));
        }

        var lease = await dailyLimitGuard.TryAcquireAsync(
            user, usageIdentity, DcaUsageKeyPrefix, ct: ct);

        try
        {
            return await CalculateCoreAsync(request, ct);
        }
        catch
        {
            await TryReleaseAsync(lease);
            throw;
        }
    }

    private async Task TryReleaseAsync(QuotaLease lease)
    {
        try
        {
            await dailyLimitGuard.ReleaseAsync(lease, CancellationToken.None);
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
        if (endDate > BusinessClock.TodayInIstanbul(timeProvider))
            throw new ValidationException(
                localizer["CalculationEndDateCannotBeInFuture"],
                field: nameof(request.EndDate));
        // SVCR-018: amountType/period için trim öncelikli — "WEEKLY " geçerli sayılır.
        var amountType = request.AmountType.Trim().ToLowerInvariant();
        var period     = request.Period.Trim().ToLowerInvariant();

        if (request.StartDate > endDate)
            throw new ValidationException(localizer["BuyDateAfterSellDate"], field: nameof(request.StartDate));

        if (period is not (PriceIntervals.Weekly or PriceIntervals.Monthly))
            throw new ValidationException(
                string.Format(localizer["InvalidPeriod"], request.Period),
                field: nameof(request.Period));

        // DCA periyodik yatırım yalnızca TL bazında anlamlı (bkz. QuantityUnits.DcaAccepted).
        if (!QuantityUnits.DcaAccepted.Contains(amountType))
            throw new ValidationException(
                string.Format(localizer["InvalidDcaAmountType"], request.AmountType),
                field: nameof(request.AmountType));

        // ── Cache kontrolü ──────────────────────────────────────────────────
        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var amountStr = request.PeriodicAmount.ToString("G", CultureInfo.InvariantCulture);
        var catalog = await assetService.GetCatalogVersionAsync(ct);
        var cacheKey = AuthorityCacheNamespace.Key(
            $"catalog:{catalog.Token}:dca:v3:{symbol}:{request.StartDate:yyyy-MM-dd}:{endDate:yyyy-MM-dd}:{amountStr}:{period}:{amountType}{inflationSuffix}:{lang}");

        var cached = await cache.TryGetAsync<DcaCacheEntry>(cacheKey, ct);
        if (cached is not null && cached.IsValid(
                symbol, request.StartDate, endDate, request.PeriodicAmount,
                period, amountType, request.IncludeInflation, lang, catalog))
            return cached.Response!;
        if (cached is not null)
            await cache.TryDeleteAsync(cacheKey, ct);

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
        // CPI dönemi, kullanıcının planladığı takvim gününden değil gerçekten fiyat
        // bulunan/alımın gerçekleştiği piyasa gününden türetilir. Aynı piyasa gününe
        // clip edilen katkılar display satırında birleşse bile burada ayrı nakit akışıdır.
        var effectiveContributions = new List<EffectiveContribution>(purchaseDates.Count);
        var skippedPurchaseDates = new List<DateOnly>();
        var priceAuthorities = new List<ObservationAuthorityValue>(purchaseDates.Count + 1);
        var inflationAuthorities = new List<ObservationAuthorityValue>();
        var dataWarnings = new List<string>();
        var cumulativeUnits = 0m;
        var cumulativeCost  = 0m;

        // Purchases and the terminal valuation share one final-authority bulk read.
        // The terminal date is included even when it duplicates the final purchase;
        // repository ordinality keeps both logical positions stable.
        var requestedPriceDates = purchaseDates.Append(endDate).ToArray();
        var pricePoints = await assetService.GetNearestPricesAsync(symbol, requestedPriceDates, ct);
        var latestPricePoint = pricePoints[^1]
            ?? throw new PriceNotFoundException(symbol, endDate);
        var purchaseIndex = 0;

        foreach (var purchaseDate in purchaseDates)
        {
            var pricePoint = pricePoints[purchaseIndex++];
            if (pricePoint is null
                || pricePoint.PriceDate > latestPricePoint.PriceDate)
            {
                skippedPurchaseDates.Add(purchaseDate);
                continue;
            }

            priceAuthorities.Add(FinalObservationAuthority.ToValue(pricePoint));
            var price         = pricePoint.Close;
            // F2.2-23 ([G-B-04]) / SVCR-016: non-positive fiyat → PriceNotFound + data bug log.
            if (price <= 0)
            {
                logger.LogWarning(
                    "DCA purchase fiyat non-positive — data bug şüphesi: {Symbol} {Date} → {Price}",
                    symbol, purchaseDate, price);
                throw new PriceNotFoundException(symbol, purchaseDate);
            }

            var calculationUnits = request.PeriodicAmount / price;
            var unitsAcquired = Math.Round(
                calculationUnits, 6, MidpointRounding.AwayFromZero);
            var investedTry = Math.Round(
                request.PeriodicAmount, 2, MidpointRounding.AwayFromZero);

            effectiveContributions.Add(new EffectiveContribution(
                ToMonth(pricePoint.PriceDate), investedTry));

            cumulativeUnits += calculationUnits;
            cumulativeCost  += investedTry;

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

        if (skippedPurchaseDates.Count > 0)
            dataWarnings.Add(AuthorityDataWarnings.PurchasePriceUnavailable);

        if (effectiveContributions.Count == 0)
            throw new PriceNotFoundException(symbol, request.StartDate);

        // ── Güncel değer ve kâr/zarar ───────────────────────────────────────
        priceAuthorities.Add(FinalObservationAuthority.ToValue(latestPricePoint));
        var currentUnitPrice = latestPricePoint.Close;
        // SVCR-004 follow-up: purchase-side ≤0 guard ile asimetri kalktı. Negatif
        // fiyat (data bug) terminalde de reddedilir; aksi halde negatif
        // `currentValueTry` ve bozuk `IsProfit` sızar. Log warning data bug
        // telemetrisini Prometheus/Aspire'a yansıtır (SVCR-016).
        if (currentUnitPrice <= 0)
        {
            logger.LogWarning(
                "DCA terminal fiyat non-positive — data bug şüphesi: {Symbol} {EndDate} → {Price}",
                symbol, endDate, currentUnitPrice);
            throw new PriceNotFoundException(symbol, endDate);
        }

        var totalUnitsAcquired = Math.Round(cumulativeUnits, 6, MidpointRounding.AwayFromZero);
        var totalInvestedTry   = Math.Round(cumulativeCost, 2, MidpointRounding.AwayFromZero);
        var terminalPortfolioValue = cumulativeUnits * currentUnitPrice;
        var currentValueTry    = Math.Round(terminalPortfolioValue, 2, MidpointRounding.AwayFromZero);
        var profitLossTry      = currentValueTry - totalInvestedTry;
        var profitLossPercent  = totalInvestedTry == 0
            ? 0m
            : Math.Round(profitLossTry / totalInvestedTry * 100, 2, MidpointRounding.AwayFromZero);

        var averageCostPerUnit = totalUnitsAcquired == 0
            ? 0m
            : Math.Round(totalInvestedTry / cumulativeUnits, 2, MidpointRounding.AwayFromZero);

        // ── Enflasyon düzeltmesi ────────────────────────────────────────────
        decimal?  cumulativeInflationPercent = null;
        decimal?  realProfitLossPercent      = null;
        decimal?  inflationAdjustedInvestedTry = null;
        decimal?  realProfitLossTry          = null;
        string?   realReturnMethod           = null;
        DateOnly? inflationTerminalMonth     = null;
        DateOnly? inflationDataAsOf          = null;
        var inflationCalculationComplete = !request.IncludeInflation;

        if (request.IncludeInflation)
        {
            realReturnMethod = RealReturnMethodCashflowCpiTerminal;
            var requestedTerminalMonth = ToMonth(latestPricePoint.PriceDate);

            try
            {
                var terminalObservation = await inflationRepository.GetLatestFinalIndexValueAsync(
                    requestedTerminalMonth, ct);
                // Exact CPI is required only before the actual final LKV month.
                // Contributions from that month through the requested terminal
                // month all use the same terminal deflator.
                var requiredExactMonths = terminalObservation is null
                    ? Array.Empty<DateOnly>()
                    : effectiveContributions
                        .Select(contribution => contribution.Month)
                        .Where(month => month < terminalObservation.PeriodDate)
                        .Distinct()
                        .ToArray();
                var indexes = await inflationRepository.GetExactIndexValuesAsync(requiredExactMonths, ct);
                var missingMonths = requiredExactMonths
                    .Where(month => !indexes.TryGetValue(month, out var index)
                                    || index.IndexValue <= 0m)
                    .OrderBy(month => month)
                    .ToArray();

                if (missingMonths.Length == 0
                    && terminalObservation is { IndexValue: > 0m })
                {
                    inflationAuthorities.AddRange(indexes.Values.Select(index => index.Authority));
                    inflationAuthorities.Add(terminalObservation.Authority);
                    var terminalIndex = terminalObservation.IndexValue;
                    inflationTerminalMonth = terminalObservation.PeriodDate;
                    var firstContribution = effectiveContributions[0];
                    var firstContributionIndex =
                        firstContribution.Month >= terminalObservation.PeriodDate
                        ? terminalIndex
                        : indexes[firstContribution.Month].IndexValue;

                    cumulativeInflationPercent = Math.Round(
                        (terminalIndex / firstContributionIndex - 1m) * 100m,
                        2,
                        MidpointRounding.AwayFromZero);

                    // Her gerçekleşebilir katkı maliyetini kendi exact CPI ayından terminal
                    // deflatör ayının satın alma gücüne taşı. Deflatör oranı ve toplam ara
                    // adımda yuvarlanmaz; TL/yüzde response sınırında yuvarlanır. Reel P/L
                    // ve ROI, iki haneye yuvarlanmış CurrentValueTry'dan değil raw terminal
                    // portföy değerinden hesaplanır.
                    var terminalAdjustedCost = effectiveContributions.Sum(contribution =>
                    {
                        var contributionIndex =
                            contribution.Month >= terminalObservation.PeriodDate
                            ? terminalIndex
                            : indexes[contribution.Month].IndexValue;
                        return contribution.InvestedTry * terminalIndex / contributionIndex;
                    });

                    inflationAdjustedInvestedTry = Math.Round(
                        terminalAdjustedCost, 2, MidpointRounding.AwayFromZero);
                    realProfitLossTry = Math.Round(
                        terminalPortfolioValue - terminalAdjustedCost,
                        2,
                        MidpointRounding.AwayFromZero);
                    realProfitLossPercent = Math.Round(
                        (terminalPortfolioValue / terminalAdjustedCost - 1m) * 100m,
                        2,
                        MidpointRounding.AwayFromZero);
                    // Legacy WhatIf semantics: null means exact target-month CPI;
                    // a value is emitted only when terminal LKV lagged the target.
                    inflationDataAsOf = terminalObservation.PeriodDate < requestedTerminalMonth
                        ? terminalObservation.PeriodDate
                        : null;
                    inflationCalculationComplete = true;
                }
                else
                {
                    logger.LogWarning(
                        "DCA reel getiri hesaplanamadı; ara exact TÜFE ayları veya terminal LKV eksik/geçersiz: {MissingMonths}",
                        string.Join(",", missingMonths.Select(month => month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
                    dataWarnings.Add(AuthorityDataWarnings.InflationIncomplete);
                }
            }
            catch (Exception ex) when (OptionalDataFailure.IsExpected(ex))
            {
                logger.LogWarning(ex, "Enflasyon hesabı başarısız, nominal getiri kullanılıyor");
                dataWarnings.Add(AuthorityDataWarnings.InflationUnavailable);
            }
        }

        // ── Chart data (max 60 nokta) ───────────────────────────────────────
        var chartData = ChartSampler.Downsample(
            purchases,
            p => new DcaChartPoint(p.Date, p.CumulativeCostTry, p.CumulativeValueTry),
            MaxChartPoints);

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
            ChartData:                  chartData,
            InflationAdjustedInvestedTry: inflationAdjustedInvestedTry,
            RealProfitLossTry:            realProfitLossTry,
            RealReturnMethod:             realReturnMethod,
            InflationTerminalMonth:       inflationTerminalMonth,
            Data: AuthorityDataResponseFactory.Calculation(
                priceAuthorities, inflationAuthorities, request.IncludeInflation, dataWarnings),
            SkippedPurchaseDates:         skippedPurchaseDates);

        // Exact CPI seti eksikse nullable reel sözleşme korunur; incomplete sonucu bir
        // saat cache'leyip yeni yayınlanan TÜFE verisini görünmez kılmayız.
        if (inflationCalculationComplete && dataWarnings.Count == 0)
            await cache.TrySetAsync(
                cacheKey,
                DcaCacheEntry.Create(
                    symbol, request.StartDate, endDate, request.PeriodicAmount,
                    period, amountType, request.IncludeInflation, lang, response, catalog),
                TimeSpan.FromHours(1), ct);

        logger.LogInformation(
            "DCA hesaplandı: {Symbol} {StartDate}→{EndDate} {Period} {AmountBucket} → {Outcome} (reel: {RealOutcome})",
            symbol, request.StartDate, endDate, period, AmountBucket.Coarse(request.PeriodicAmount),
            TelemetryOutcome.From(profitLossTry), TelemetryOutcome.From(realProfitLossTry));

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
        if (period == PriceIntervals.Weekly)
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

    private static DateOnly ToMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private sealed record EffectiveContribution(DateOnly Month, decimal InvestedTry);
}
