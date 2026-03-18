using System.Text.Json;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Services;

public sealed class DcaCalculator(
    IAssetService assetService,
    ISavedScenarioRepository scenarioRepository,
    IInflationRepository inflationRepository,
    IConnectionMultiplexer redis,
    IOptions<PlanOptions> options,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<DcaCalculator> logger) : IDcaCalculator
{
    private const string PremiumTier         = "premium";
    private const string DcaUsageKeyPrefix   = "usage:whatif:";
    private const int    MaxChartPoints      = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<DcaResponse> CalculateAsync(string deviceId, DcaRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        await CheckDailyLimitAsync(user, deviceId);

        var response = await CalculateCoreAsync(request, ct);

        await IncrementAndEnforceLimitAsync(user, deviceId);
        return response;
    }

    private async Task<DcaResponse> CalculateCoreAsync(DcaRequest request, CancellationToken ct)
    {
        var symbol     = request.AssetSymbol.ToUpperInvariant();
        var endDate    = request.EndDate
            ?? await assetService.GetLatestPriceDateAsync(symbol, ct);
        var amountType = request.AmountType.ToLowerInvariant();
        var period     = request.Period.ToLowerInvariant();

        if (request.StartDate > endDate)
            throw new ArgumentException(localizer["BuyDateAfterSellDate"]);

        if (period is not ("weekly" or "monthly"))
            throw new ArgumentException(
                string.Format(localizer["InvalidAmountType"], request.Period));

        if (amountType is not "try")
            throw new ArgumentException(
                string.Format(localizer["InvalidAmountType"], request.AmountType));

        // ── Cache kontrolü ──────────────────────────────────────────────────
        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var cacheKey = $"dca:v1:{symbol}:{request.StartDate:yyyy-MM-dd}:{endDate:yyyy-MM-dd}:{request.PeriodicAmount}:{period}:{amountType}{inflationSuffix}:{lang}";

        var cached = await TryGetCachedAsync<DcaResponse>(cacheKey);
        if (cached is not null)
            return cached;

        // ── Asset bilgisi ───────────────────────────────────────────────────
        var assets = await assetService.GetAllAsync(ct);
        var asset  = assets.FirstOrDefault(a => a.Symbol == symbol)
            ?? throw new PriceNotFoundException(symbol, request.StartDate);

        // ── Alım tarihlerini oluştur ────────────────────────────────────────
        var purchaseDates = GeneratePurchaseDates(request.StartDate, endDate, period);

        if (purchaseDates.Count == 0)
            throw new ArgumentException(localizer["BuyDateAfterSellDate"]);

        // ── Her alım tarihi için hesaplama ──────────────────────────────────
        var purchases      = new List<DcaPurchase>(purchaseDates.Count);
        var cumulativeUnits = 0m;
        var cumulativeCost  = 0m;

        foreach (var purchaseDate in purchaseDates)
        {
            var pricePoint    = await assetService.GetNearestPriceAsync(symbol, purchaseDate, ct);
            var price         = pricePoint.Close;
            var unitsAcquired = Math.Round(request.PeriodicAmount / price, 6, MidpointRounding.AwayFromZero);

            cumulativeUnits += unitsAcquired;
            cumulativeCost  += request.PeriodicAmount;

            var cumulativeValue = Math.Round(cumulativeUnits * price, 2, MidpointRounding.AwayFromZero);

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
                var (buyIdx, buyIdxDate, sellIdx, sellIdxDate) =
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
            AssetDisplayName:           LocalizeAssetName(symbol, asset.DisplayName),
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

        await TrySetCacheAsync(cacheKey, response, TimeSpan.FromHours(1));

        logger.LogInformation(
            "DCA hesaplandı: {Symbol} {StartDate}→{EndDate} {Period} {Amount} → %{ProfitLossPercent} (reel: %{RealProfitLossPercent})",
            symbol, request.StartDate, endDate, period, request.PeriodicAmount,
            profitLossPercent, realProfitLossPercent?.ToString() ?? "-");

        return response;
    }

    private static List<DateOnly> GeneratePurchaseDates(DateOnly startDate, DateOnly endDate, string period)
    {
        var dates   = new List<DateOnly>();
        var current = startDate;

        while (current <= endDate)
        {
            dates.Add(current);
            current = period == "weekly"
                ? current.AddDays(7)
                : current.AddMonths(1);
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

    private string LocalizeAssetName(string symbol, string fallbackDisplayName)
    {
        var localized = localizer[$"Asset_{symbol}"];
        return localized.ResourceNotFound ? fallbackDisplayName : localized.Value;
    }

    // ── Redis yardımcıları ────────────────────────────────────────────────────

    private async Task<T?> TryGetCachedAsync<T>(string key) where T : class
    {
        try
        {
            var db    = redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            if (!value.HasValue) return null;
            return JsonSerializer.Deserialize<T>(value.ToString(), JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis okuma hatası: {Key}", key);
            return null;
        }
    }

    private async Task TrySetCacheAsync<T>(string key, T value, TimeSpan ttl)
    {
        try
        {
            var db = redis.GetDatabase();
            await db.StringSetAsync(key, JsonSerializer.Serialize(value, JsonOptions), ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis yazma hatası: {Key}", key);
        }
    }

    // ── Günlük limit yönetimi (WhatIfCalculator ile aynı pattern) ─────────

    private async Task CheckDailyLimitAsync(Saydin.Shared.Entities.User? user, string deviceId)
    {
        if (user?.Tier == PremiumTier) return;

        var limit = options.Value.GetTierOptions(user?.Tier).DailyCalculationLimit;
        if (limit <= 0) return;

        var key = BuildUsageKey(user, deviceId);
        try
        {
            var db    = redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            var count = value.HasValue ? (long)value : 0;

            if (count >= limit)
                throw new DailyLimitExceededException(limit);
        }
        catch (Exception ex) when (ex is not DailyLimitExceededException)
        {
            logger.LogWarning(ex, "Daily limit Redis kontrolü başarısız, hesaplama devam ediyor");
        }
    }

    private async Task IncrementAndEnforceLimitAsync(Saydin.Shared.Entities.User? user, string deviceId)
    {
        if (user?.Tier == PremiumTier) return;

        var limit = options.Value.GetTierOptions(user?.Tier).DailyCalculationLimit;
        if (limit <= 0) return;

        var key   = BuildUsageKey(user, deviceId);
        var ttlMs = (long)(DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalMilliseconds;
        try
        {
            const string script = """
                local count = redis.call('INCR', KEYS[1])
                if count == 1 then
                  redis.call('PEXPIRE', KEYS[1], ARGV[1])
                end
                if tonumber(count) > tonumber(ARGV[2]) then
                  redis.call('DECR', KEYS[1])
                  return -1
                end
                return count
                """;
            var result = (long)await redis.GetDatabase()
                .ScriptEvaluateAsync(script, keys: [key], values: [ttlMs, limit]);

            if (result == -1)
                throw new DailyLimitExceededException(limit);
        }
        catch (Exception ex) when (ex is not DailyLimitExceededException)
        {
            logger.LogWarning(ex, "Daily limit increment başarısız: {Key}", key);
        }
    }

    private static string BuildUsageKey(Saydin.Shared.Entities.User? user, string deviceId)
    {
        var userId  = user?.Id.ToString() ?? deviceId;
        var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return $"{DcaUsageKeyPrefix}{userId}:{dateKey}";
    }
}
