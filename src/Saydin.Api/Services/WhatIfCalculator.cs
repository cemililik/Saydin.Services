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

public sealed class WhatIfCalculator(
    IAssetService assetService,
    ISavedScenarioRepository scenarioRepository,
    IInflationRepository inflationRepository,
    IDailyLimitGuard dailyLimitGuard,
    IRedisCacheHelper cache,
    IAssetNameLocalizer assetNameLocalizer,
    IOptions<PlanOptions> options,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<WhatIfCalculator> logger) : IWhatIfCalculator
{
    private const string WhatIfUsageKeyPrefix = "usage:whatif:";
    private const int    MaxPriceHistoryPoints = 60;

    public async Task<WhatIfResponse> CalculateAsync(string deviceId, WhatIfRequest request, CancellationToken ct)
    {
        EnsureRequest(request);
        EnsureDeviceId(deviceId);
        EnsureRequired(request.AssetSymbol, nameof(request.AssetSymbol));
        EnsureRequired(request.AmountType, nameof(request.AmountType));
        EnsurePositive(request.Amount, nameof(request.Amount));

        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        var features = options.Value.GetTierOptions(user?.Tier).Features;

        if (request.IncludeInflation && !features.InflationAdjustment)
            throw new FeatureDisabledException(localizer["FeatureDisabled"], featureKey: "inflation");

        // Önce atomik reserve — TOCTOU race kapatıldı. Pahalı hesap öncesi limit dayatılır.
        await dailyLimitGuard.TryAcquireAsync(user, deviceId, WhatIfUsageKeyPrefix, ct: ct);
        try
        {
            return await CalculateCoreAsync(request, ct);
        }
        catch
        {
            // Hesap başarısız → kotayı iade et ("başarısız hesap kotadan düşmesin").
            // Release fırlatırsa orijinal exception'ı maskelemesin: best-effort + log.
            await TryReleaseAsync(user, deviceId, WhatIfUsageKeyPrefix);
            throw;
        }
    }

    public async Task<CompareResponse> CompareAsync(string deviceId, CompareRequest request, CancellationToken ct)
    {
        EnsureRequest(request);
        EnsureDeviceId(deviceId);

        if (request.AssetSymbols is null)
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], nameof(request.AssetSymbols)),
                field: nameof(request.AssetSymbols));
        EnsureRequired(request.AmountType, nameof(request.AmountType));
        EnsurePositive(request.Amount, nameof(request.Amount));

        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);

        var features = options.Value.GetTierOptions(user?.Tier).Features;
        if (!features.Comparison)
            throw new FeatureDisabledException(localizer["FeatureDisabled"], featureKey: "comparison");

        // CompareAsync 5 sembolü tek kullanım sayar; ancak inflation tier kuralı CalculateAsync
        // ile aynı: özellik kapalıysa request bayrağını sessizce yok say.
        var includeInflation = request.IncludeInflation && features.InflationAdjustment;

        // Tekrarlanan semboller kaldırıldıktan sonra 2-5 arasında unique sembol gerekli
        var symbols = request.AssetSymbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbols.Count is < 2 or > 5)
            throw new ValidationException(localizer["CompareSymbolCount"], field: nameof(request.AssetSymbols));

        await dailyLimitGuard.TryAcquireAsync(user, deviceId, WhatIfUsageKeyPrefix, ct: ct);

        try
        {
            // Paralel hesap — DB/cache erişimleri bağımsız, 5 sembol için ~5x hızlanma.
            // Kota Lua script ile atomik tutulduğu için single Acquire yeterli (fair-use:
            // compare = 1 işlem; docs/architecture'da belgelenmeli).
            var tasks = symbols.Select(symbol => CalculateCoreAsync(new WhatIfRequest(
                AssetSymbol:       symbol,
                BuyDate:           request.BuyDate,
                SellDate:          request.SellDate,
                Amount:            request.Amount,
                AmountType:        request.AmountType,
                IncludeInflation:  includeInflation), ct)).ToArray();

            var results = await Task.WhenAll(tasks);

            // Karlılığa göre sırala (en yüksek ProfitLossPercent → Rank 1)
            var ranked = results
                .OrderByDescending(r => r.ProfitLossPercent)
                .Select((r, i) => new CompareResultItem(Rank: i + 1, Calculation: r))
                .ToList();

            logger.LogInformation(
                "Karşılaştırma hesaplandı: {Symbols} {BuyDate}→{SellDate}",
                string.Join(",", symbols), request.BuyDate, request.SellDate);

            return new CompareResponse(ranked);
        }
        catch
        {
            await TryReleaseAsync(user, deviceId, WhatIfUsageKeyPrefix);
            throw;
        }
    }

    public async Task<ReverseWhatIfResponse> CalculateReverseAsync(
        string deviceId, ReverseWhatIfRequest request, CancellationToken ct)
    {
        EnsureRequest(request);
        EnsureDeviceId(deviceId);
        EnsureRequired(request.AssetSymbol, nameof(request.AssetSymbol));
        EnsureRequired(request.TargetAmountType, nameof(request.TargetAmountType));
        EnsurePositive(request.TargetAmount, nameof(request.TargetAmount));

        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        var features = options.Value.GetTierOptions(user?.Tier).Features;

        if (request.IncludeInflation && !features.InflationAdjustment)
            throw new FeatureDisabledException(localizer["FeatureDisabled"], featureKey: "inflation");

        await dailyLimitGuard.TryAcquireAsync(user, deviceId, WhatIfUsageKeyPrefix, ct: ct);
        try
        {
            return await CalculateReverseCoreAsync(request, ct);
        }
        catch
        {
            await TryReleaseAsync(user, deviceId, WhatIfUsageKeyPrefix);
            throw;
        }
    }

    private async Task TryReleaseAsync(User? user, string deviceId, string usageKeyPrefix)
    {
        try
        {
            await dailyLimitGuard.ReleaseAsync(user, deviceId, usageKeyPrefix, ct: CancellationToken.None);
        }
        catch (Exception releaseEx)
        {
            // Release best-effort: orijinal exception kullanıcıya yansımalı, release hatası loglanır.
            logger.LogWarning(releaseEx, "Daily limit release başarısız: {Prefix}", usageKeyPrefix);
        }
    }

    private async Task<ReverseWhatIfResponse> CalculateReverseCoreAsync(
        ReverseWhatIfRequest request, CancellationToken ct)
    {
        var symbol           = request.AssetSymbol.ToUpperInvariant();
        var sellDate         = request.SellDate
            ?? await assetService.GetLatestPriceDateAsync(symbol, ct);
        var targetAmountType = request.TargetAmountType.ToLowerInvariant();

        if (request.BuyDate > sellDate)
            throw new ValidationException(localizer["BuyDateAfterSellDate"], field: nameof(request.BuyDate));

        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var amountStr = request.TargetAmount.ToString("G", CultureInfo.InvariantCulture);
        var cacheKey = $"whatif:reverse:v1:{symbol}:{request.BuyDate:yyyy-MM-dd}:{sellDate:yyyy-MM-dd}:{amountStr}:{targetAmountType}{inflationSuffix}:{lang}";

        var cached = await cache.TryGetAsync<ReverseWhatIfResponse>(cacheKey, ct);
        if (cached is not null)
            return cached;

        var buyPricePoint  = await assetService.GetNearestPriceAsync(symbol, request.BuyDate, ct);
        var sellPricePoint = await assetService.GetNearestPriceAsync(symbol, sellDate, ct);

        var actualBuyDate  = buyPricePoint.PriceDate  != request.BuyDate ? buyPricePoint.PriceDate  : (DateOnly?)null;
        var actualSellDate = sellPricePoint.PriceDate != sellDate         ? sellPricePoint.PriceDate : (DateOnly?)null;

        var asset = await assetService.GetBySymbolAsync(symbol, ct)
            ?? throw new AssetNotFoundException(symbol);

        var buyPrice  = buyPricePoint.Close;
        var sellPrice = sellPricePoint.Close;

        // Ters hesaplama: hedef son değerden gereken başlangıç yatırımını bul
        decimal targetValueTry;
        decimal unitsAcquired;
        decimal requiredInvestmentTry;

        if (sellPrice == 0)
            throw new PriceNotFoundException(symbol, sellDate);

        switch (targetAmountType)
        {
            case "try":
                // Hedef TL değeri → kaç birim lazım → kaç TL yatırmalıydın
                targetValueTry      = request.TargetAmount;
                unitsAcquired       = Math.Round(request.TargetAmount / sellPrice, 6, MidpointRounding.AwayFromZero);
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
                throw new ValidationException(
                    string.Format(localizer["InvalidAmountType"], request.TargetAmountType),
                    field: nameof(request.TargetAmountType));
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
                var (buyIdx, _, sellIdx, sellIdxDate) =
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
            AssetDisplayName:           assetNameLocalizer.Localize(symbol, asset.DisplayName),
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

        await cache.TrySetAsync(cacheKey, response, TimeSpan.FromHours(1), ct);

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
            throw new ValidationException(localizer["BuyDateAfterSellDate"], field: nameof(request.BuyDate));

        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        // Decimal formatlamasını kültür-bağımsız tutuyoruz (tr-TR'de virgül üretmesin → cache key fragmentation).
        var amountStr = request.Amount.ToString("G", CultureInfo.InvariantCulture);
        var cacheKey = $"whatif:v3:{symbol}:{request.BuyDate:yyyy-MM-dd}:{sellDate:yyyy-MM-dd}:{amountStr}:{amountType}{inflationSuffix}:{lang}";

        var cached = await cache.TryGetAsync<WhatIfResponse>(cacheKey, ct);
        if (cached is not null)
            return cached;

        // Fiyatlar AssetService üzerinden gelir — Redis cache'li
        // Haftasonu/tatil durumunda en yakın işlem günü kullanılır (±7 gün)
        var buyPricePoint  = await assetService.GetNearestPriceAsync(symbol, request.BuyDate, ct);
        var sellPricePoint = await assetService.GetNearestPriceAsync(symbol, sellDate, ct);

        // Kullanıcının seçtiği tarih ile fiilen kullanılan tarih farklıysa bildir
        var actualBuyDate  = buyPricePoint.PriceDate  != request.BuyDate ? buyPricePoint.PriceDate  : (DateOnly?)null;
        var actualSellDate = sellPricePoint.PriceDate != sellDate         ? sellPricePoint.PriceDate : (DateOnly?)null;

        var asset = await assetService.GetBySymbolAsync(symbol, ct)
            ?? throw new AssetNotFoundException(symbol);

        var buyPrice  = buyPricePoint.Close;
        var sellPrice = sellPricePoint.Close;

        // Bozuk veri / sıfır fiyat: divide-by-zero yerine PriceNotFound → 404 (review C-2).
        if (buyPrice == 0)
            throw new PriceNotFoundException(symbol, request.BuyDate);

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
                throw new ValidationException(
                    string.Format(localizer["InvalidAmountType"], request.AmountType),
                    field: nameof(request.AmountType));
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
                var (buyIdx, _, sellIdx, sellIdxDate) =
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
            AssetDisplayName:           assetNameLocalizer.Localize(symbol, asset.DisplayName),
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

        await cache.TrySetAsync(cacheKey, response, TimeSpan.FromHours(1), ct);

        // Nullable decimal'i doğrudan structured field olarak geç — null tutarlı yansır.
        logger.LogInformation(
            "WhatIf hesaplandı: {Symbol} {BuyDate}→{SellDate} {AmountType}:{Amount} → %{ProfitLossPercent} (reel: %{RealProfitLossPercent})",
            symbol, request.BuyDate, sellDate, amountType, request.Amount,
            profitLossPercent, realProfitLossPercent);

        return response;
    }

    private void EnsureRequired(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], field), field: field);
    }

    // P1R-003: ArgumentException base type framework / altyapı tarafından da fırlatılır
    // (Redis bağlantı yapılandırma, EF Core, Npgsql vs.). Endpoint katmanından gelen
    // request/deviceId guard'larını domain ValidationException'a çevirerek
    // ValidationExceptionHandler'ın ArgumentException case'ini kaldırabilir hale getirdik.
    private void EnsureRequest(object? request)
    {
        if (request is null)
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], "request"), field: "request");
    }

    private void EnsureDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ValidationException(localizer["DeviceIdRequiredDetail"], field: "deviceId");
    }

    // F1.9-4 ([C-F-14]): Negatif / sıfır amount semantik olarak anlamsız —
    // "ya 0 TL alsaydım?" pratik bir hesap değil. Validation tüm calculator
    // entry-point'lerinde aynı şekilde uygulanır.
    private void EnsurePositive(decimal value, string field)
    {
        if (value <= 0m)
            throw new ValidationException(localizer["AmountMustBePositive"], field: field);
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
}
