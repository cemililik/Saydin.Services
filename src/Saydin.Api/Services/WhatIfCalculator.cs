using System.Text.Json;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;

using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Services;

public sealed class WhatIfCalculator(
    IAssetService assetService,
    ISavedScenarioRepository scenarioRepository,
    IInflationRepository inflationRepository,
    IConnectionMultiplexer redis,
    IOptions<PlanOptions> options,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<WhatIfCalculator> logger) : IWhatIfCalculator
{
    private const string PremiumTier          = "premium";
    private const string WhatIfUsageKeyPrefix = "usage:whatif:";
    private const int    MaxPriceHistoryPoints = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<WhatIfResponse> CalculateAsync(string deviceId, WhatIfRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        await CheckDailyLimitAsync(user, deviceId);

        var response = await CalculateCoreAsync(request, ct);

        // Atomik check+increment: başarılı hesaplamalar kotadan düşülür
        await IncrementAndEnforceLimitAsync(user, deviceId);
        return response;
    }

    public async Task<CompareResponse> CompareAsync(string deviceId, CompareRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (request.AssetSymbols.Count is < 2 or > 5)
            throw new ArgumentException(localizer["CompareSymbolCount"]);

        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        await CheckDailyLimitAsync(user, deviceId);

        // DbContext scoped olduğu için paralel çalıştırılamaz; sıralı çalıştırılır.
        // Redis cache'i sayesinde tekrar eden semboller hızla yanıtlanır.
        var symbols = request.AssetSymbols
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resultList = new List<WhatIfResponse>(symbols.Count);
        foreach (var symbol in symbols)
        {
            var item = await CalculateCoreAsync(new WhatIfRequest(
                AssetSymbol:       symbol,
                BuyDate:           request.BuyDate,
                SellDate:          request.SellDate,
                Amount:            request.Amount,
                AmountType:        request.AmountType,
                IncludeInflation:  request.IncludeInflation), ct);
            resultList.Add(item);
        }

        var results = resultList.ToArray();

        // Karlılığa göre sırala (en yüksek ProfitLossPercent → Rank 1)
        var ranked = results
            .OrderByDescending(r => r.ProfitLossPercent)
            .Select((r, i) => new CompareResultItem(Rank: i + 1, Calculation: r))
            .ToList();

        // Karşılaştırma da 1 hak olarak sayılır (atomik check+increment)
        await IncrementAndEnforceLimitAsync(user, deviceId);

        logger.LogInformation(
            "Karşılaştırma hesaplandı: {Symbols} {BuyDate}→{SellDate}",
            string.Join(",", request.AssetSymbols), request.BuyDate, request.SellDate);

        return new CompareResponse(ranked);
    }

    public async Task<ReverseWhatIfResponse> CalculateReverseAsync(
        string deviceId, ReverseWhatIfRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        await CheckDailyLimitAsync(user, deviceId);

        var response = await CalculateReverseCoreAsync(request, ct);

        await IncrementAndEnforceLimitAsync(user, deviceId);
        return response;
    }

    private async Task<ReverseWhatIfResponse> CalculateReverseCoreAsync(
        ReverseWhatIfRequest request, CancellationToken ct)
    {
        var symbol           = request.AssetSymbol.ToUpperInvariant();
        var sellDate         = request.SellDate
            ?? await assetService.GetLatestPriceDateAsync(symbol, ct);
        var targetAmountType = request.TargetAmountType.ToLowerInvariant();

        if (request.BuyDate > sellDate)
            throw new ArgumentException(localizer["BuyDateAfterSellDate"]);

        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var cacheKey = $"whatif:reverse:v1:{symbol}:{request.BuyDate:yyyy-MM-dd}:{sellDate:yyyy-MM-dd}:{request.TargetAmount}:{targetAmountType}{inflationSuffix}:{lang}";

        var cached = await TryGetCachedAsync<ReverseWhatIfResponse>(cacheKey);
        if (cached is not null)
            return cached;

        var buyPricePoint  = await assetService.GetNearestPriceAsync(symbol, request.BuyDate, ct);
        var sellPricePoint = await assetService.GetNearestPriceAsync(symbol, sellDate, ct);

        var actualBuyDate  = buyPricePoint.PriceDate  != request.BuyDate ? buyPricePoint.PriceDate  : (DateOnly?)null;
        var actualSellDate = sellPricePoint.PriceDate != sellDate         ? sellPricePoint.PriceDate : (DateOnly?)null;

        var assets = await assetService.GetAllAsync(ct);
        var asset  = assets.FirstOrDefault(a => a.Symbol == symbol)
            ?? throw new PriceNotFoundException(symbol, request.BuyDate);

        var buyPrice  = buyPricePoint.Close;
        var sellPrice = sellPricePoint.Close;

        // Ters hesaplama: hedef son değerden gereken başlangıç yatırımını bul
        decimal targetValueTry;
        decimal unitsAcquired;
        decimal requiredInvestmentTry;

        switch (targetAmountType)
        {
            case "try":
                // Hedef TL değeri → kaç birim lazım → kaç TL yatırmalıydın
                targetValueTry      = request.TargetAmount;
                unitsAcquired       = sellPrice == 0
                    ? 0m
                    : Math.Round(request.TargetAmount / sellPrice, 6, MidpointRounding.AwayFromZero);
                requiredInvestmentTry = Math.Round(unitsAcquired * buyPrice, 2, MidpointRounding.AwayFromZero);
                break;
            case "units":
            case "grams":
                // Hedef birim/gram sayısı → son değer TL → gereken TL
                unitsAcquired       = request.TargetAmount;
                targetValueTry      = Math.Round(request.TargetAmount * sellPrice, 2, MidpointRounding.AwayFromZero);
                requiredInvestmentTry = Math.Round(request.TargetAmount * buyPrice, 2, MidpointRounding.AwayFromZero);
                break;
            default:
                throw new ArgumentException(
                    string.Format(localizer["InvalidAmountType"], request.TargetAmountType));
        }

        var profitLossTry     = targetValueTry - requiredInvestmentTry;
        var profitLossPercent = requiredInvestmentTry == 0
            ? 0m
            : Math.Round(profitLossTry / requiredInvestmentTry * 100, 2, MidpointRounding.AwayFromZero);

        IReadOnlyList<PriceHistoryPoint> priceHistory;
        try
        {
            var range = await assetService.GetPriceRangeAsync(symbol, request.BuyDate, sellDate, "daily", ct);
            priceHistory = SamplePriceHistory(range);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fiyat geçmişi alınamadı: {Symbol}", symbol);
            priceHistory = Array.Empty<PriceHistoryPoint>();
        }

        // ── Enflasyon düzeltmesi ────────────────────────────────────────────
        decimal?  cumulativeInflationPercent = null;
        decimal?  realProfitLossPercent      = null;
        DateOnly? inflationDataAsOf          = null;

        if (request.IncludeInflation)
        {
            try
            {
                var (buyIdx, buyIdxDate, sellIdx, sellIdxDate) =
                    await inflationRepository.GetIndexValuesAsync(request.BuyDate, sellDate, ct);

                if (buyIdx is not null && sellIdx is not null && buyIdx != 0)
                {
                    cumulativeInflationPercent = Math.Round(
                        (sellIdx.Value / buyIdx.Value - 1m) * 100, 2, MidpointRounding.AwayFromZero);

                    var nominalFactor   = 1m + profitLossPercent / 100m;
                    var inflationFactor = 1m + cumulativeInflationPercent.Value / 100m;
                    realProfitLossPercent = Math.Round(
                        (nominalFactor / inflationFactor - 1m) * 100, 2, MidpointRounding.AwayFromZero);

                    var expectedSellMonth = new DateOnly(sellDate.Year, sellDate.Month, 1);
                    if (sellIdxDate.HasValue && sellIdxDate.Value < expectedSellMonth)
                        inflationDataAsOf = sellIdxDate;
                }
                else
                {
                    logger.LogWarning(
                        "Enflasyon endeksi bulunamadı: {BuyDate} / {SellDate}", request.BuyDate, sellDate);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Enflasyon hesabı başarısız, nominal getiri kullanılıyor");
            }
        }

        var response = new ReverseWhatIfResponse(
            AssetSymbol:                symbol,
            AssetDisplayName:           LocalizeAssetName(symbol, asset.DisplayName),
            BuyDate:                    request.BuyDate,
            SellDate:                   sellDate,
            BuyPrice:                   buyPrice,
            SellPrice:                  sellPrice,
            RequiredInvestmentTry:      requiredInvestmentTry,
            UnitsAcquired:              unitsAcquired,
            TargetValueTry:             targetValueTry,
            ProfitLossTry:              profitLossTry,
            ProfitLossPercent:          profitLossPercent,
            IsProfit:                   profitLossTry >= 0,
            PriceHistory:               priceHistory,
            CumulativeInflationPercent: cumulativeInflationPercent,
            RealProfitLossPercent:      realProfitLossPercent,
            InflationDataAsOf:          inflationDataAsOf,
            ActualBuyDate:              actualBuyDate,
            ActualSellDate:             actualSellDate
        );

        await TrySetCacheAsync(cacheKey, response, TimeSpan.FromHours(1));

        logger.LogInformation(
            "Reverse WhatIf hesaplandı: {Symbol} {BuyDate}→{SellDate} hedef:{TargetAmountType}:{TargetAmount} → gereken: ₺{RequiredInvestment} %{ProfitLossPercent}",
            symbol, request.BuyDate, sellDate, targetAmountType, request.TargetAmount,
            requiredInvestmentTry, profitLossPercent);

        return response;
    }

    private async Task<WhatIfResponse> CalculateCoreAsync(WhatIfRequest request, CancellationToken ct)
    {
        var symbol     = request.AssetSymbol.ToUpperInvariant();
        var sellDate   = request.SellDate
            ?? await assetService.GetLatestPriceDateAsync(symbol, ct);
        var amountType = request.AmountType.ToLowerInvariant();

        if (request.BuyDate > sellDate)
            throw new ArgumentException(localizer["BuyDateAfterSellDate"]);

        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var cacheKey = $"whatif:v3:{symbol}:{request.BuyDate:yyyy-MM-dd}:{sellDate:yyyy-MM-dd}:{request.Amount}:{amountType}{inflationSuffix}:{lang}";

        var cached = await TryGetCachedAsync<WhatIfResponse>(cacheKey);
        if (cached is not null)
            return cached;

        // Fiyatlar AssetService üzerinden gelir — Redis cache'li
        // Haftasonu/tatil durumunda en yakın işlem günü kullanılır (±7 gün)
        var buyPricePoint  = await assetService.GetNearestPriceAsync(symbol, request.BuyDate, ct);
        var sellPricePoint = await assetService.GetNearestPriceAsync(symbol, sellDate, ct);

        // Kullanıcının seçtiği tarih ile fiilen kullanılan tarih farklıysa bildir
        var actualBuyDate  = buyPricePoint.PriceDate  != request.BuyDate ? buyPricePoint.PriceDate  : (DateOnly?)null;
        var actualSellDate = sellPricePoint.PriceDate != sellDate         ? sellPricePoint.PriceDate : (DateOnly?)null;

        var assets = await assetService.GetAllAsync(ct);
        var asset  = assets.FirstOrDefault(a => a.Symbol == symbol)
            ?? throw new PriceNotFoundException(symbol, request.BuyDate);

        var buyPrice  = buyPricePoint.Close;
        var sellPrice = sellPricePoint.Close;

        decimal initialValueTry;
        decimal unitsAcquired;

        switch (amountType)
        {
            case "try":
                initialValueTry = request.Amount;
                unitsAcquired   = Math.Round(request.Amount / buyPrice, 6, MidpointRounding.AwayFromZero);
                break;
            case "units":
            case "grams":
                unitsAcquired   = request.Amount;
                initialValueTry = Math.Round(request.Amount * buyPrice, 2, MidpointRounding.AwayFromZero);
                break;
            default:
                throw new ArgumentException(
                    string.Format(localizer["InvalidAmountType"], request.AmountType));
        }

        var finalValueTry     = Math.Round(unitsAcquired * sellPrice, 2, MidpointRounding.AwayFromZero);
        var profitLossTry     = finalValueTry - initialValueTry;
        var profitLossPercent = initialValueTry == 0
            ? 0m
            : Math.Round(profitLossTry / initialValueTry * 100, 2, MidpointRounding.AwayFromZero);

        IReadOnlyList<PriceHistoryPoint> priceHistory;
        try
        {
            var range = await assetService.GetPriceRangeAsync(symbol, request.BuyDate, sellDate, "daily", ct);
            priceHistory = SamplePriceHistory(range);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fiyat geçmişi alınamadı: {Symbol}", symbol);
            priceHistory = Array.Empty<PriceHistoryPoint>();
        }

        // ── Enflasyon düzeltmesi ────────────────────────────────────────────
        decimal?  cumulativeInflationPercent = null;
        decimal?  realProfitLossPercent      = null;
        DateOnly? inflationDataAsOf          = null;

        if (request.IncludeInflation)
        {
            try
            {
                var (buyIdx, buyIdxDate, sellIdx, sellIdxDate) =
                    await inflationRepository.GetIndexValuesAsync(request.BuyDate, sellDate, ct);

                if (buyIdx is not null && sellIdx is not null && buyIdx != 0)
                {
                    // Birikimli enflasyon: (satış_endeksi / alış_endeksi) - 1
                    cumulativeInflationPercent = Math.Round(
                        (sellIdx.Value / buyIdx.Value - 1m) * 100, 2, MidpointRounding.AwayFromZero);

                    // Fisher denklemi: reel_getiri = (1 + nominal) / (1 + enflasyon) - 1
                    var nominalFactor   = 1m + profitLossPercent / 100m;
                    var inflationFactor = 1m + cumulativeInflationPercent.Value / 100m;
                    realProfitLossPercent = Math.Round(
                        (nominalFactor / inflationFactor - 1m) * 100, 2, MidpointRounding.AwayFromZero);

                    // Satış ayının tam verisi yoksa (TÜİK gecikmesi) gerçek tarih bildirilir
                    var expectedSellMonth = new DateOnly(sellDate.Year, sellDate.Month, 1);
                    if (sellIdxDate.HasValue && sellIdxDate.Value < expectedSellMonth)
                        inflationDataAsOf = sellIdxDate;
                }
                else
                {
                    logger.LogWarning(
                        "Enflasyon endeksi bulunamadı: {BuyDate} / {SellDate}", request.BuyDate, sellDate);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Enflasyon hesabı başarısız, nominal getiri kullanılıyor");
            }
        }

        var response = new WhatIfResponse(
            AssetSymbol:                symbol,
            AssetDisplayName:           LocalizeAssetName(symbol, asset.DisplayName),
            BuyDate:                    request.BuyDate,
            SellDate:                   sellDate,
            BuyPrice:                   buyPrice,
            SellPrice:                  sellPrice,
            UnitsAcquired:              unitsAcquired,
            InitialValueTry:            initialValueTry,
            FinalValueTry:              finalValueTry,
            ProfitLossTry:              profitLossTry,
            ProfitLossPercent:          profitLossPercent,
            IsProfit:                   profitLossTry >= 0,
            PriceHistory:               priceHistory,
            CumulativeInflationPercent: cumulativeInflationPercent,
            RealProfitLossPercent:      realProfitLossPercent,
            InflationDataAsOf:          inflationDataAsOf,
            ActualBuyDate:              actualBuyDate,
            ActualSellDate:             actualSellDate
        );

        await TrySetCacheAsync(cacheKey, response, TimeSpan.FromHours(1));

        logger.LogInformation(
            "WhatIf hesaplandı: {Symbol} {BuyDate}→{SellDate} {AmountType}:{Amount} → %{ProfitLossPercent} (reel: %{RealProfitLossPercent})",
            symbol, request.BuyDate, sellDate, amountType, request.Amount,
            profitLossPercent, realProfitLossPercent?.ToString() ?? "-");

        return response;
    }

    private string LocalizeAssetName(string symbol, string fallbackDisplayName)
    {
        var localized = localizer[$"Asset_{symbol}"];
        return localized.ResourceNotFound ? fallbackDisplayName : localized.Value;
    }

    private static IReadOnlyList<PriceHistoryPoint> SamplePriceHistory(
        IReadOnlyList<PricePoint> points, int maxPoints = MaxPriceHistoryPoints)
    {
        if (points.Count == 0) return Array.Empty<PriceHistoryPoint>();
        if (points.Count <= maxPoints)
            return points.Select(p => new PriceHistoryPoint(p.PriceDate, p.Close)).ToList();

        var result = new List<PriceHistoryPoint>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
        {
            var idx = Math.Min((int)((double)i * (points.Count - 1) / (maxPoints - 1)), points.Count - 1);
            result.Add(new PriceHistoryPoint(points[idx].PriceDate, points[idx].Close));
        }
        return result;
    }

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

    private async Task CheckDailyLimitAsync(User? user, string deviceId)
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

    /// <summary>
    /// Atomik olarak sayacı artırır ve limiti aşıyorsa -1 döner.
    /// Check + increment tek Lua script'te yapılarak race condition önlenir.
    /// </summary>
    private async Task IncrementAndEnforceLimitAsync(User? user, string deviceId)
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

    private static string BuildUsageKey(User? user, string deviceId)
    {
        var userId  = user?.Id.ToString() ?? deviceId;
        var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return $"{WhatIfUsageKeyPrefix}{userId}:{dateKey}";
    }
}
